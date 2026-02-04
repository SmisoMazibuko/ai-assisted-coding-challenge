using Microsoft.Extensions.Logging;
using FluentResults;
using ExchangeRate.Core.Exceptions;
using ExchangeRate.Core.Helpers;
using ExchangeRate.Core.Interfaces;
using ExchangeRate.Core.Interfaces.Providers;
using ExchangeRate.Core.Entities;
using ExchangeRate.Core.Enums;
using ExchangeRate.Core.Infrastructure;

namespace ExchangeRate.Core
{
    class ExchangeRateRepository : IExchangeRateRepository
    {
        private static readonly IEnumerable<ExchangeRateSources> SupportedSources = System.Enum.GetValues(typeof(ExchangeRateSources)).Cast<ExchangeRateSources>().ToList();

        /// <summary>
        /// Maps currecy code string to currency type.
        /// </summary>
        private static readonly Dictionary<string, CurrencyTypes> CurrencyMapping;

        private readonly Dictionary<(ExchangeRateSources, ExchangeRateFrequencies), Dictionary<CurrencyTypes, Dictionary<DateTime, decimal>>> _fxRatesBySourceFrequencyAndCurrency;
        private Dictionary<(ExchangeRateSources, ExchangeRateFrequencies), DateTime> _minFxDateBySourceAndFrequency;
        private readonly Dictionary<CurrencyTypes, PeggedCurrency> _peggedCurrencies;

        /// <summary>
        /// Tracks which months have been fully loaded to prevent redundant API calls.
        /// Key: (Source, Frequency, Year, Month)
        /// </summary>
        private readonly HashSet<(ExchangeRateSources, ExchangeRateFrequencies, int Year, int Month)> _loadedMonths;

        private readonly IExchangeRateDataStore _dataStore;

        private readonly ILogger<ExchangeRateRepository> _logger;
        private readonly IExchangeRateProviderFactory _exchangeRateSourceFactory;

        static ExchangeRateRepository()
        {
            var currencies = System.Enum.GetValues(typeof(CurrencyTypes)).Cast<CurrencyTypes>().ToList();
            CurrencyMapping = currencies.ToDictionary(x => x.ToString().ToUpperInvariant());
        }

        private void ResetMinFxDates()
        {
            _minFxDateBySourceAndFrequency = SupportedSources.SelectMany(x => new List<(ExchangeRateSources, ExchangeRateFrequencies)>
            {
                new (x, ExchangeRateFrequencies.Daily),
                new (x, ExchangeRateFrequencies.Monthly),
                new (x, ExchangeRateFrequencies.Weekly),
                new (x, ExchangeRateFrequencies.BiWeekly),
            }).ToDictionary(x => x, _ => DateTime.MaxValue);
        }

        public ExchangeRateRepository(IExchangeRateDataStore dataStore, ILogger<ExchangeRateRepository> logger, IExchangeRateProviderFactory exchangeRateSourceFactory)
        {
            _dataStore = dataStore ?? throw new ArgumentNullException(nameof(dataStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _exchangeRateSourceFactory = exchangeRateSourceFactory ?? throw new ArgumentNullException(nameof(exchangeRateSourceFactory));

            _fxRatesBySourceFrequencyAndCurrency = new Dictionary<(ExchangeRateSources, ExchangeRateFrequencies), Dictionary<CurrencyTypes, Dictionary<DateTime, decimal>>>();
            _loadedMonths = new HashSet<(ExchangeRateSources, ExchangeRateFrequencies, int, int)>();
            ResetMinFxDates();

            _peggedCurrencies = _dataStore.GetPeggedCurrencies()
                .ToDictionary(x => x.CurrencyId!.Value);
        }

        internal ExchangeRateRepository(IEnumerable<Entities.ExchangeRate> rates, IExchangeRateProviderFactory exchangeRateSourceFactory)
        {
            _fxRatesBySourceFrequencyAndCurrency = new Dictionary<(ExchangeRateSources, ExchangeRateFrequencies), Dictionary<CurrencyTypes, Dictionary<DateTime, decimal>>>();
            _loadedMonths = new HashSet<(ExchangeRateSources, ExchangeRateFrequencies, int, int)>();
            _peggedCurrencies = new Dictionary<CurrencyTypes, PeggedCurrency>();
            ResetMinFxDates();

            LoadRates(rates);

            _exchangeRateSourceFactory = exchangeRateSourceFactory ?? throw new ArgumentNullException(nameof(exchangeRateSourceFactory));
        }

        /// <summary>
        /// Returns the exchange rate for the <paramref name="toCurrency"/> on the given <paramref name="date"/>.
        /// It will return a previously valid rate, if the database does not contain rate for the specified <paramref name="date"/>.
        /// It will return NULL if there is no rate at all for the <paramref name="toCurrency"/>.
        ///
        /// KEY IMPROVEMENT: Uses month-based caching to minimize external API calls.
        /// When a rate is missing, fetches the entire month at once.
        /// </summary>
        public decimal? GetRate(CurrencyTypes fromCurrency, CurrencyTypes toCurrency, DateTime date, ExchangeRateSources source, ExchangeRateFrequencies frequency)
        {
            var provider = _exchangeRateSourceFactory.GetExchangeRateProvider(source);

            if (toCurrency == fromCurrency)
                return 1m;

            date = date.Date;

            // Ensure the month is loaded (single API call for entire month)
            EnsureMonthLoaded(source, frequency, date);

            var minFxDate = GetMinFxDate(date, source, frequency);

            // If neither fromCurrency, nor toCurrency matches the provider's currency, we need to calculate cross rates
            if (fromCurrency != provider.Currency && toCurrency != provider.Currency)
            {
                return GetRate(fromCurrency, provider.Currency, date, source, frequency) * GetRate(provider.Currency, toCurrency, date, source, frequency);
            }

            CurrencyTypes lookupCurrency = default;
            var result = GetFxRate(GetRatesByCurrency(source, frequency), date, minFxDate, provider, fromCurrency, toCurrency, out _);

            if (result.IsSuccess)
                return result.Value;

            // If no fx rate found for date, update rates in case some dates are missing between minFxDate and tax point date
            if (result.Errors.FirstOrDefault() is NoFxRateFoundError)
            {
                UpdateRates(provider, minFxDate, date, source, frequency);

                result = GetFxRate(GetRatesByCurrency(source, frequency), date, minFxDate, provider, fromCurrency,
                    toCurrency, out var currency);

                if (result.IsSuccess)
                    return result.Value;

                lookupCurrency = currency;
            }

            _logger?.LogError("No {source} {frequency} exchange rate found for {lookupCurrency} on {date:yyyy-MM-dd}. Earliest available date: {minFxDate:yyyy-MM-dd}. FromCurrency: {fromCurrency}, ToCurrency: {toCurrency}", source, frequency, lookupCurrency, date, minFxDate, fromCurrency, toCurrency);
            return null;
        }

        /// <summary>
        /// Returns the exchange rate for the <paramref name="currencyCode"/> on the given <paramref name="date"/>.
        /// It will return a previously valid rate, if the database does not contain rate for the specified <paramref name="date"/>.
        /// It will return NULL if there is no rate at all for the <paramref name="currencyCode"/>.
        /// </summary>
        public decimal? GetRate(string fromCurrencyCode, string toCurrencyCode, DateTime date, ExchangeRateSources source, ExchangeRateFrequencies frequency)
        {
            var fromCurrency = GetCurrencyType(fromCurrencyCode);

            var toCurrency = GetCurrencyType(toCurrencyCode);

            return GetRate(fromCurrency, toCurrency, date, source, frequency);
        }

        /// <summary>
        /// Updates the exchange rates for the last available day/month.
        /// </summary>
        public void UpdateRates()
        {
            foreach (var source in _exchangeRateSourceFactory.ListExchangeRateSources())
            {
                try
                {
                    var provider = _exchangeRateSourceFactory.GetExchangeRateProvider(source);

                    var rates = new List<Entities.ExchangeRate>();

                    if (provider is IDailyExchangeRateProvider dailyProvider)
                        rates.AddRange(dailyProvider.GetDailyFxRates().ToList());

                    if (provider is IMonthlyExchangeRateProvider monthlyProvider)
                        rates.AddRange(monthlyProvider.GetMonthlyFxRates().ToList());

                    if (provider is IWeeklyExchangeRateProvider weeklyProvider)
                        rates.AddRange(weeklyProvider.GetWeeklyFxRates().ToList());

                    if (provider is IBiWeeklyExchangeRateProvider biWeeklyProvider)
                        rates.AddRange(biWeeklyProvider.GetBiWeeklyFxRates().ToList());

                    if (rates.Any())
                    {
                        LoadRatesFromDb(PeriodHelper.GetStartOfMonth(rates.Min(x => x.Date!.Value)));

                        var itemsToSave = new List<Entities.ExchangeRate>();
                        foreach (var rate in rates)
                        {
                            if (AddRateToDictionaries(rate))
                                itemsToSave.Add(rate);
                        }

                        if (itemsToSave.Any())
                            _dataStore.SaveExchangeRatesAsync(itemsToSave).GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update daily rates for {source}", source.ToString());
                }
            }
        }

        /// <summary>
        /// Ensures that the database contains all exchange rates after <paramref name="minDate"/>.
        /// </summary>
        public bool EnsureMinimumDateRange(DateTime minDate, IEnumerable<ExchangeRateSources> exchangeRateSources = null)
        {
            var result = true;
            foreach (var source in exchangeRateSources ?? _exchangeRateSourceFactory.ListExchangeRateSources())
            {
                var provider = _exchangeRateSourceFactory.GetExchangeRateProvider(source);
                if (provider is IDailyExchangeRateProvider &&
                    !EnsureMinimumDateRange(minDate, source, ExchangeRateFrequencies.Daily))
                {
                    result = false;
                }

                if (provider is IMonthlyExchangeRateProvider &&
                    !EnsureMinimumDateRange(minDate, source, ExchangeRateFrequencies.Monthly))
                {
                    result = false;
                }

                if (provider is IWeeklyExchangeRateProvider &&
                    !EnsureMinimumDateRange(minDate, source, ExchangeRateFrequencies.Weekly))
                {
                    result = false;
                }

                if (provider is IBiWeeklyExchangeRateProvider &&
                    !EnsureMinimumDateRange(minDate, source, ExchangeRateFrequencies.BiWeekly))
                {
                    result = false;
                }
            }

            return result;
        }

        /// <summary>
        /// Ensures that the database contains all exchange rates after <paramref name="minDate"/> for the given <paramref name="source"/> and <paramref name="frequency"/>.
        /// </summary>
        private bool EnsureMinimumDateRange(DateTime minDate, ExchangeRateSources source, ExchangeRateFrequencies frequency)
        {
            minDate = PeriodHelper.GetStartOfMonth(minDate);

            var minFxDate = PeriodHelper.GetStartOfMonth(_minFxDateBySourceAndFrequency[(source, frequency)]);

            // if the minimum exchange rate date is lower than or equal to the specified date, then we don't need to update the rates
            if (minFxDate <= minDate)
                return true;

            LoadRatesFromDb(minDate);

            minFxDate = PeriodHelper.GetStartOfMonth(_minFxDateBySourceAndFrequency[(source, frequency)]);
            if (minFxDate <= minDate)
                return true;

            var provider = _exchangeRateSourceFactory.GetExchangeRateProvider(source);

            return EnsureMinimumDateRange(provider, minDate, source, frequency);
        }

        private bool EnsureMinimumDateRange(IExchangeRateProvider provider, DateTime minDate, ExchangeRateSources source, ExchangeRateFrequencies frequency)
        {
            if (!_minFxDateBySourceAndFrequency.TryGetValue((source, frequency), out var minFxDate))
                throw new ExchangeRateException($"Couldn't find min FX date for source {source} with frequency {frequency}");

            // if the minimum exchange rate date is lower than or equal to the specified date, then we don't need to update the rates
            if (minFxDate <= minDate)
                return true;

            if (minFxDate == DateTime.MaxValue)
            {
                minFxDate = DateTime.UtcNow.Date;
            }

            // if there would still be missing FX rates, we need to collect them from the historical data source
            return UpdateRates(provider, minDate, minFxDate, source, frequency);
        }

        private bool UpdateRates(IExchangeRateProvider provider, DateTime minDate, DateTime minFxDate, ExchangeRateSources source, ExchangeRateFrequencies frequency)
        {
            var itemsToSave = new List<Entities.ExchangeRate>();

            // Ensure dates are in the correct chronological order (from <= to)
            var from = minDate <= minFxDate ? minDate : minFxDate;
            var to = minDate <= minFxDate ? minFxDate : minDate;

            switch (frequency)
            {
                case ExchangeRateFrequencies.Daily:
                    if (provider is not IDailyExchangeRateProvider dailyProvider)
                        throw new ExchangeRateException($"Provider {provider} does not support frequency {frequency}");
                    itemsToSave.AddRange(dailyProvider.GetHistoricalDailyFxRates(from, to).ToList());
                    break;
                case ExchangeRateFrequencies.Monthly:
                    if (provider is not IMonthlyExchangeRateProvider monthlyProvider)
                        throw new ExchangeRateException($"Provider {provider} does not support frequency {frequency}");
                    itemsToSave.AddRange(monthlyProvider.GetHistoricalMonthlyFxRates(from, to).ToList());
                    break;
                case ExchangeRateFrequencies.Weekly:
                    if (provider is not IWeeklyExchangeRateProvider weeklyProvider)
                        throw new ExchangeRateException($"Provider {provider} does not support frequency {frequency}");
                    itemsToSave.AddRange(weeklyProvider.GetHistoricalWeeklyFxRates(from, to).ToList());
                    break;
                case ExchangeRateFrequencies.BiWeekly:
                    if (provider is not IBiWeeklyExchangeRateProvider biweeklyProvider)
                        throw new ExchangeRateException($"Provider {provider} does not support frequency {frequency}");
                    itemsToSave.AddRange(biweeklyProvider.GetHistoricalBiWeeklyFxRates(from, to).ToList());
                    break;
                default:
                    throw new ExchangeRateException($"Unsupported frequency: {frequency}");
            }

            if (itemsToSave.Count == 0)
            {
                _logger.LogError("No historical data found between date {minDate:yyyy-MM-dd} and {v:yyyy-MM-dd} for source {source} with frequency {frequency}.", minDate, minFxDate, source, frequency);
                return false;
            }

            var newMinFxDate = minFxDate;
            foreach (var item in itemsToSave.ToArray())
            {
                if (!AddRateToDictionaries(item))
                    itemsToSave.Remove(item);

                if (item.Date!.Value < newMinFxDate)
                    newMinFxDate = item.Date.Value;
            }
            _minFxDateBySourceAndFrequency[(source, frequency)] = newMinFxDate;

            // if storing in memory was successful, we can save it to the database
            if (itemsToSave.Any())
                _dataStore.SaveExchangeRatesAsync(itemsToSave).GetAwaiter().GetResult();

            return true;
        }

        /// <summary>
        /// Loads FX rates into cache dictionary starting with the specified date and sets the <see cref="_minFxDate"/>.
        /// </summary>
        private void LoadRatesFromDb(DateTime minDate)
        {
            var minFxDate = _minFxDateBySourceAndFrequency.Min(x => x.Value);
            var fxRatesInDb = _dataStore.GetExchangeRatesAsync(minDate, minFxDate).GetAwaiter().GetResult();

            LoadRates(fxRatesInDb);
        }

        /// <summary>
        /// Loads FX rates into cache dictionary starting with the specified date and sets the <see cref="_minFxDateBySourceAndFrequency"/>.
        /// </summary>
        private void LoadRates(IEnumerable<Entities.ExchangeRate> fxRatesInDb)
        {
            // store them in memory and refresh minimum FX rate date
            var minFxDateBySource = _minFxDateBySourceAndFrequency;
            foreach (var item in fxRatesInDb)
            {
                AddRateToDictionaries(item);

                var source = item.Source!.Value;
                var frequency = item.Frequency!.Value;

                if (!minFxDateBySource.TryGetValue((source, frequency), out var minFxDate))
                    throw new ExchangeRateException($"Couldn't find min FX date for source {source} with frequency {frequency}");

                if (item.Date!.Value < minFxDate)
                    minFxDateBySource[(source, frequency)] = item.Date.Value;
            }

            _minFxDateBySourceAndFrequency = minFxDateBySource.ToDictionary(x => x.Key, x => x.Value);
        }

        /// <summary>
        /// Adds or updates exchange rates in the FX rate dictionaries.
        /// Supports upsert semantics to allow rate corrections from banks.
        /// Returns true if the rate was added or updated (needs persistence).
        /// </summary>
        private bool AddRateToDictionaries(Entities.ExchangeRate item)
        {
            var currency = item.CurrencyId!.Value;
            var date = item.Date!.Value;
            var source = item.Source!.Value;
            var frequency = item.Frequency!.Value;
            var newRate = item.Rate!.Value;

            if (!_fxRatesBySourceFrequencyAndCurrency.TryGetValue((source, frequency), out var currenciesBySource))
                _fxRatesBySourceFrequencyAndCurrency.Add((source, frequency), currenciesBySource = new Dictionary<CurrencyTypes, Dictionary<DateTime, decimal>>());

            if (!currenciesBySource.TryGetValue(currency, out var datesByCurrency))
                currenciesBySource.Add(currency, datesByCurrency = new Dictionary<DateTime, decimal>());

            if (datesByCurrency.TryGetValue(date, out var savedRate))
            {
                var roundedNew = decimal.Round(newRate, Entities.ExchangeRate.Precision);
                var roundedSaved = decimal.Round(savedRate, Entities.ExchangeRate.Precision);

                if (roundedNew != roundedSaved)
                {
                    // Rate correction - log for audit trail and update the rate
                    _logger?.LogInformation(
                        "Rate correction applied: {Source} {Frequency} {Currency} {Date:yyyy-MM-dd} " +
                        "changed from {OldRate} to {NewRate}",
                        source, frequency, currency, date, savedRate, newRate);

                    // Update the rate (upsert semantics)
                    datesByCurrency[date] = newRate;
                    return true; // Signal that persistence is needed
                }

                return false; // No change needed
            }
            else
            {
                datesByCurrency.Add(date, newRate);
                return true;
            }
        }

        private Result<decimal> GetFxRate(
            IReadOnlyDictionary<CurrencyTypes, Dictionary<DateTime, decimal>> ratesByCurrencyAndDate,
            DateTime date,
            DateTime minFxDate,
            IExchangeRateProvider provider,
            CurrencyTypes fromCurrency,
            CurrencyTypes toCurrency,
            out CurrencyTypes lookupCurrency)
        {
                // Handle same-currency conversion (can happen in recursive pegged currency lookups)
                if (fromCurrency == toCurrency)
                {
                    lookupCurrency = fromCurrency;
                    return Result.Ok(1m);
                }

                //  always need to find the rate for the currency that is not the provider's currency
                lookupCurrency = toCurrency == provider.Currency ? fromCurrency : toCurrency;
                var nonLookupCurrency = toCurrency == provider.Currency ? toCurrency : fromCurrency;

                if (!ratesByCurrencyAndDate.TryGetValue(lookupCurrency, out var currencyDict))
                {
                    if (!_peggedCurrencies.TryGetValue(lookupCurrency, out var peggedCurrency))
                    {
                        return Result.Fail(new NotSupportedCurrencyError(lookupCurrency));
                    }

                    var peggedToCurrencyResult = GetFxRate(ratesByCurrencyAndDate, date, minFxDate, provider, nonLookupCurrency, peggedCurrency.PeggedTo!.Value, out _);

                    if (peggedToCurrencyResult.IsFailed)
                    {
                        return peggedToCurrencyResult;
                    }

                    var peggedRate = peggedCurrency.Rate!.Value;
                    var resultRate = peggedToCurrencyResult.Value;

                    return Result.Ok(toCurrency == provider.Currency
                        ? peggedRate / resultRate
                        : resultRate / peggedRate);

                }
                // start looking for the date, and decreasing the date if no match found (but only until the minFxDate)

            for (var d = date; d >= minFxDate; d = d.AddDays(-1d))
            {
                if (currencyDict.TryGetValue(d, out var fxRate))
                {
                    /*
                       If your local currency is EUR:
                       - Direct exchange rate: 1 USD = 0.92819 EUR
                       - Indirect exchange rate: 1 EUR = 1.08238 USD
                    */

                    // QuoteType    ProviderCurrency    FromCurrency    ToCurrency    Rate
                    // Direct       EUR                 USD             EUR           fxRate
                    // Direct       EUR                 EUR             USD           1/fxRate
                    // InDirect     EUR                 USD             EUR           1/fxRate
                    // InDirect     EUR                 EUR             USD           fxRate

                    return provider.QuoteType switch
                    {
                        QuoteTypes.Direct when toCurrency == provider.Currency => Result.Ok(fxRate),
                        QuoteTypes.Direct when fromCurrency == provider.Currency => Result.Ok(1 / fxRate),
                        QuoteTypes.Indirect when fromCurrency == provider.Currency => Result.Ok(fxRate),
                        QuoteTypes.Indirect when toCurrency == provider.Currency => Result.Ok(1 / fxRate),
                        _ => throw new InvalidOperationException("Unsupported QuoteType")
                    };
                }
            }

            return Result.Fail(new NoFxRateFoundError());
        }

        private static CurrencyTypes GetCurrencyType(string currencyCode)
        {
            if (string.IsNullOrWhiteSpace(currencyCode))
                throw new ExchangeRateException("Null or empty currency code.");

            if (!CurrencyMapping.TryGetValue(currencyCode.ToUpperInvariant(), out var currency))
                throw new ExchangeRateException("Not supported currency code: " + currencyCode);

            return currency;
        }

        private DateTime GetMinFxDate(DateTime date, ExchangeRateSources source, ExchangeRateFrequencies frequency)
        {
            if (!_minFxDateBySourceAndFrequency.TryGetValue((source, frequency), out var minFxDate))
                throw new ExchangeRateException("Couldn't find base min FX date for source: " + source);

            // if the currently available date is higher than the requested date, then we need to get it from the database, or fill the database from the FX rate source
            if (minFxDate > date)
                EnsureMinimumDateRange(date.AddMonths(-1), source, frequency);

            // Update minFxDate value after EnsureMinimumDateRange
            _minFxDateBySourceAndFrequency.TryGetValue((source, frequency), out minFxDate);

            return minFxDate;
        }

        private IReadOnlyDictionary<CurrencyTypes, Dictionary<DateTime, decimal>> GetRatesByCurrency(ExchangeRateSources source, ExchangeRateFrequencies frequency)
        {
            if (!_fxRatesBySourceFrequencyAndCurrency.TryGetValue((source, frequency), out var ratesByCurrency))
                throw new ExchangeRateException(
                    $"No exchange rates available for source {source} with frequency {frequency}");

            return ratesByCurrency;
        }

        #region Month-Based Caching

        /// <summary>
        /// Ensures all rates for the given month are loaded into cache.
        /// This minimizes API calls by fetching entire months at once.
        /// </summary>
        private void EnsureMonthLoaded(ExchangeRateSources source, ExchangeRateFrequencies frequency, DateTime date)
        {
            var monthKey = (source, frequency, date.Year, date.Month);

            // Already loaded this month
            if (_loadedMonths.Contains(monthKey))
                return;

            var monthStart = PeriodHelper.GetStartOfMonth(date);
            var monthEnd = monthStart.AddMonths(1);

            // First, try to load from database
            var dbRates = _dataStore?.GetExchangeRatesAsync(monthStart, monthEnd)
                .GetAwaiter().GetResult()
                .Where(r => r.Source == source && r.Frequency == frequency) ?? Enumerable.Empty<Entities.ExchangeRate>();

            foreach (var rate in dbRates)
                AddRateToDictionaries(rate);

            // If no rates found for this month, fetch from provider
            if (!HasRatesForMonth(source, frequency, monthStart))
            {
                FetchAndStoreMonth(source, frequency, monthStart);
            }

            _loadedMonths.Add(monthKey);
        }

        /// <summary>
        /// Checks if we have any cached rates for the given month.
        /// </summary>
        private bool HasRatesForMonth(ExchangeRateSources source, ExchangeRateFrequencies frequency, DateTime monthStart)
        {
            if (!_fxRatesBySourceFrequencyAndCurrency.TryGetValue((source, frequency), out var currencies))
                return false;

            var monthEnd = monthStart.AddMonths(1);
            return currencies.Values.Any(dates =>
                dates.Keys.Any(d => d >= monthStart && d < monthEnd));
        }

        /// <summary>
        /// Fetches rates for an entire month from the provider and stores them.
        /// </summary>
        private void FetchAndStoreMonth(ExchangeRateSources source, ExchangeRateFrequencies frequency, DateTime monthStart)
        {
            var provider = _exchangeRateSourceFactory.GetExchangeRateProvider(source);
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);

            var rates = FetchRatesFromProvider(provider, monthStart, monthEnd, frequency).ToList();

            if (rates.Count == 0)
            {
                _logger?.LogWarning(
                    "No rates returned from {Source} for {Year}-{Month:00}",
                    source, monthStart.Year, monthStart.Month);
                return;
            }

            var itemsToSave = new List<Entities.ExchangeRate>();

            foreach (var rate in rates)
            {
                if (AddRateToDictionaries(rate))
                    itemsToSave.Add(rate);
            }

            if (itemsToSave.Count > 0 && _dataStore != null)
            {
                _dataStore.SaveExchangeRatesAsync(itemsToSave).GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// Fetches rates from the appropriate provider method based on frequency.
        /// </summary>
        private static IEnumerable<Entities.ExchangeRate> FetchRatesFromProvider(
            IExchangeRateProvider provider,
            DateTime from,
            DateTime to,
            ExchangeRateFrequencies frequency)
        {
            return frequency switch
            {
                ExchangeRateFrequencies.Daily when provider is IDailyExchangeRateProvider daily
                    => daily.GetHistoricalDailyFxRates(from, to),
                ExchangeRateFrequencies.Monthly when provider is IMonthlyExchangeRateProvider monthly
                    => monthly.GetHistoricalMonthlyFxRates(from, to),
                ExchangeRateFrequencies.Weekly when provider is IWeeklyExchangeRateProvider weekly
                    => weekly.GetHistoricalWeeklyFxRates(from, to),
                ExchangeRateFrequencies.BiWeekly when provider is IBiWeeklyExchangeRateProvider biweekly
                    => biweekly.GetHistoricalBiWeeklyFxRates(from, to),
                _ => Enumerable.Empty<Entities.ExchangeRate>()
            };
        }

        #endregion
    }

    class NotSupportedCurrencyError : Error
    {
        public NotSupportedCurrencyError(CurrencyTypes currency)
            : base("Not supported currency: " + currency) { }
    }

    class NoFxRateFoundError : Error
    {
        public NoFxRateFoundError()
            : base("No fx rate found") { }
    }
}
