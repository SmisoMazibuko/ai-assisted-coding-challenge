# Exchange Rate System Refactoring - Architecture Plan

**Candidate:** Simiso Mazibuko
**Date:** February 4, 2026
**Challenge:** Taxually AI-Assisted Coding Challenge
**AI Tools Used:** Claude Code (Opus 4.5)

---

## Executive Summary

This document outlines a refactoring plan for the Exchange Rate management system. The refactoring addresses critical business requirements (rate corrections, efficient API usage) while improving code maintainability and simplicity.

**Key Business Requirements (from job description):**
1. Transactions processed serially, not in batches
2. Data typically within the same month - fetch/cache full month at a time
3. External providers have strict rate limits - minimize HTTP requests
4. System must support rate corrections (overwrite existing rates)

---

## Current State Analysis

### Problems Identified

#### 1. Rate Correction Impossible (CRITICAL)
**Location:** `ExchangeRateRepository.cs:370-376`

```csharp
if (datesByCurrency.TryGetValue(date, out var savedRate))
{
    if (decimal.Round(newRate, Precision) != decimal.Round(savedRate, Precision))
    {
        _logger.LogError(...);
        throw new ExchangeRateException(...); // BLOCKS ALL CORRECTIONS!
    }
    return false;
}
```

**Impact:** Banks frequently send corrected rates after initial publication. The current system throws an exception when receiving a corrected rate, requiring manual intervention or cache clear.

**Business Requirement Violated:** "We frequently receive corrected rates from banks after the fact. The system must be able to overwrite/update an existing rate in the database without requiring a full cache clear or manual intervention."

#### 2. Inefficient API Calls (Rate Limit Risk)
**Location:** `ExchangeRateRepository.cs:257-313`

Current behavior when a rate is missing:
1. Fetches historical data with `GetHistoricalDailyFxRates(from, to)`
2. For daily rates, this iterates day-by-day or in chunks
3. Does NOT implement the recommended month-based fetch strategy

**Business Requirement Violated:** "If a rate is missing, fetch the whole month to minimize HTTP requests."

#### 3. Monolithic Repository (506 lines)
The `ExchangeRateRepository` class handles multiple responsibilities:
- In-memory caching
- Database persistence coordination
- Rate fetching from providers
- Rate calculation (direct/indirect quotes)
- Pegged currency logic
- Cross-currency triangulation
- Date fallback logic

This violates the Single Responsibility Principle and makes the code hard to comprehend and modify.

#### 4. Complex Nested Data Structure
```csharp
Dictionary<(ExchangeRateSources, ExchangeRateFrequencies),
    Dictionary<CurrencyTypes,
        Dictionary<DateTime, decimal>>>
```

This triple-nested dictionary is:
- Hard to reason about
- Error-prone for lookups
- Not thread-safe

#### 5. Sync-over-Async Anti-pattern
**Location:** `AsyncUtil.RunSync()` used throughout

```csharp
var rates = AsyncUtil.RunSync(() => GetDailyRatesAsync(BankId, period));
```

This blocks thread pool threads and risks deadlocks.

---

## Proposed Solution

### Design Principles
1. **Single Responsibility**: Each class does one thing well
2. **Simplicity First**: The simplest solution that works
3. **Minimal Changes**: Preserve test compatibility
4. **Business Requirements First**: Fix the critical issues

### Architecture Overview

```
+---------------------------------------------------------------+
|                         API Layer                              |
|                    GET /api/rates                              |
+---------------------------------------------------------------+
                              |
                              v
+---------------------------------------------------------------+
|                  ExchangeRateService                           |
|  - Single entry point for rate queries                         |
|  - Coordinates cache, fetcher, and calculator                  |
+---------------------------------------------------------------+
                              |
           +------------------+------------------+
           v                  v                  v
+-----------------+  +-----------------+  +-----------------+
|  RateCache      |  |  RateFetcher    |  | RateCalculator  |
|                 |  |                 |  |                 |
| - Month-based   |  | - Fetches full  |  | - Quote type    |
|   storage       |  |   month on miss |  |   conversion    |
| - Upsert logic  |  | - Provider      |  | - Cross-rates   |
| - Thread-safe   |  |   abstraction   |  | - Pegged rates  |
+-----------------+  +-----------------+  +-----------------+
           |                  |
           v                  v
+-----------------+  +-----------------+
|  DataStore      |  |   Providers     |
|  (persistence)  |  |   (external)    |
+-----------------+  +-----------------+
```

### Phased Implementation

```
+---------------------------------------------------------------+
| Phase 1: Enable Rate Corrections (CRITICAL - ~30 mins)         |
| - Modify AddRateToDictionaries to support upsert               |
| - Add audit logging for rate changes                           |
| - Update data store to support upserts                         |
+---------------------------------------------------------------+
                              |
                              v
+---------------------------------------------------------------+
| Phase 2: Month-Based Caching (~1 hour)                         |
| - Track loaded months per (source, frequency)                  |
| - On cache miss, fetch entire month                            |
| - Minimize provider API calls                                  |
+---------------------------------------------------------------+
                              |
                              v
+---------------------------------------------------------------+
| Phase 3: Simplify Cache Structure (~45 mins)                   |
| - Replace nested dictionaries with flat ConcurrentDictionary   |
| - Use composite key (Source, Frequency, Currency, Date)        |
| - Improve thread safety                                        |
+---------------------------------------------------------------+
                              |
                              v
+---------------------------------------------------------------+
| Phase 4: Extract Rate Calculator (Optional - ~30 mins)         |
| - Move quote type conversion to separate class                 |
| - Move cross-rate calculation to separate class                |
| - Improve testability                                          |
+---------------------------------------------------------------+
```

---

## Detailed Implementation Plan

### Phase 1: Enable Rate Corrections

**Goal:** Allow the system to accept corrected rates without throwing exceptions.

**File:** `ExchangeRateRepository.cs`

**Before (lines 354-383):**
```csharp
private bool AddRateToDictionaries(Entities.ExchangeRate item)
{
    // ... get values ...

    if (datesByCurrency.TryGetValue(date, out var savedRate))
    {
        if (decimal.Round(newRate, Precision) != decimal.Round(savedRate, Precision))
        {
            _logger.LogError("Saved exchange rate differs from new value...");
            throw new ExchangeRateException(...);
        }
        return false;
    }
    else
    {
        datesByCurrency.Add(date, newRate);
        return true;
    }
}
```

**After:**
```csharp
private bool AddRateToDictionaries(Entities.ExchangeRate item)
{
    var currency = item.CurrencyId!.Value;
    var date = item.Date!.Value;
    var source = item.Source!.Value;
    var frequency = item.Frequency!.Value;
    var newRate = item.Rate!.Value;

    if (!_fxRatesBySourceFrequencyAndCurrency.TryGetValue((source, frequency), out var currenciesBySource))
        _fxRatesBySourceFrequencyAndCurrency.Add((source, frequency),
            currenciesBySource = new Dictionary<CurrencyTypes, Dictionary<DateTime, decimal>>());

    if (!currenciesBySource.TryGetValue(currency, out var datesByCurrency))
        currenciesBySource.Add(currency, datesByCurrency = new Dictionary<DateTime, decimal>());

    if (datesByCurrency.TryGetValue(date, out var savedRate))
    {
        var roundedNew = decimal.Round(newRate, Entities.ExchangeRate.Precision);
        var roundedSaved = decimal.Round(savedRate, Entities.ExchangeRate.Precision);

        if (roundedNew != roundedSaved)
        {
            // Log the correction for audit trail
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
```

**File:** `Program.cs` (InMemoryExchangeRateDataStore)

**Before (lines 115-130):**
```csharp
public Task SaveExchangeRatesAsync(IEnumerable<ExchangeRate.Core.Entities.ExchangeRate> rates)
{
    foreach (var rate in rates)
    {
        var existingRate = _exchangeRates.FirstOrDefault(r =>
            r.Date == rate.Date &&
            r.CurrencyId == rate.CurrencyId &&
            r.Source == rate.Source &&
            r.Frequency == rate.Frequency);

        if (existingRate == null)
        {
            _exchangeRates.Add(rate);
        }
    }
    return Task.CompletedTask;
}
```

**After:**
```csharp
public Task SaveExchangeRatesAsync(IEnumerable<ExchangeRate.Core.Entities.ExchangeRate> rates)
{
    foreach (var rate in rates)
    {
        var existingIndex = _exchangeRates.FindIndex(r =>
            r.Date == rate.Date &&
            r.CurrencyId == rate.CurrencyId &&
            r.Source == rate.Source &&
            r.Frequency == rate.Frequency);

        if (existingIndex >= 0)
        {
            // Upsert: replace existing rate with new value
            _exchangeRates[existingIndex] = rate;
        }
        else
        {
            _exchangeRates.Add(rate);
        }
    }
    return Task.CompletedTask;
}
```

---

### Phase 2: Month-Based Caching

**Goal:** When a rate is missing, fetch the entire month to minimize API calls.

**New field in ExchangeRateRepository:**
```csharp
private readonly HashSet<(ExchangeRateSources, ExchangeRateFrequencies, int Year, int Month)> _loadedMonths = new();
```

**New helper method:**
```csharp
private void EnsureMonthLoaded(ExchangeRateSources source, ExchangeRateFrequencies frequency, DateTime date)
{
    var monthKey = (source, frequency, date.Year, date.Month);

    if (_loadedMonths.Contains(monthKey))
        return; // Already loaded

    // First, try to load from database
    var monthStart = new DateTime(date.Year, date.Month, 1);
    var monthEnd = monthStart.AddMonths(1);

    var ratesFromDb = _dataStore.GetExchangeRatesAsync(monthStart, monthEnd)
        .GetAwaiter().GetResult()
        .Where(r => r.Source == source && r.Frequency == frequency);

    foreach (var rate in ratesFromDb)
        AddRateToDictionaries(rate);

    // If still no rates for this month, fetch from provider
    if (!HasAnyRatesForMonth(source, frequency, date))
    {
        var provider = _exchangeRateSourceFactory.GetExchangeRateProvider(source);
        var monthEndDate = monthStart.AddMonths(1).AddDays(-1);

        var newRates = FetchRatesFromProvider(provider, monthStart, monthEndDate, frequency);
        var itemsToSave = new List<Entities.ExchangeRate>();

        foreach (var rate in newRates)
        {
            if (AddRateToDictionaries(rate))
                itemsToSave.Add(rate);
        }

        if (itemsToSave.Any())
            _dataStore.SaveExchangeRatesAsync(itemsToSave).GetAwaiter().GetResult();
    }

    _loadedMonths.Add(monthKey);
}

private bool HasAnyRatesForMonth(ExchangeRateSources source, ExchangeRateFrequencies frequency, DateTime date)
{
    if (!_fxRatesBySourceFrequencyAndCurrency.TryGetValue((source, frequency), out var currencies))
        return false;

    var monthStart = new DateTime(date.Year, date.Month, 1);
    var monthEnd = monthStart.AddMonths(1);

    return currencies.Values.Any(dates =>
        dates.Keys.Any(d => d >= monthStart && d < monthEnd));
}

private IEnumerable<Entities.ExchangeRate> FetchRatesFromProvider(
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
```

**Modified GetRate flow:**
```csharp
public decimal? GetRate(CurrencyTypes fromCurrency, CurrencyTypes toCurrency, DateTime date,
    ExchangeRateSources source, ExchangeRateFrequencies frequency)
{
    if (toCurrency == fromCurrency)
        return 1m;

    date = date.Date;
    var provider = _exchangeRateSourceFactory.GetExchangeRateProvider(source);

    // Ensure the month is loaded (single API call for entire month)
    EnsureMonthLoaded(source, frequency, date);

    // Rest of the method remains the same...
}
```

---

### Phase 3: Simplify Cache Structure

**Goal:** Replace nested dictionaries with a flat, thread-safe structure.

**New key type:**
```csharp
public readonly record struct RateKey(
    ExchangeRateSources Source,
    ExchangeRateFrequencies Frequency,
    CurrencyTypes Currency,
    DateTime Date);
```

**New cache field:**
```csharp
private readonly ConcurrentDictionary<RateKey, decimal> _rateCache = new();
private readonly ConcurrentDictionary<(ExchangeRateSources, ExchangeRateFrequencies, int, int), byte> _loadedMonths = new();
```

**Simplified AddRate:**
```csharp
private bool AddOrUpdateRate(Entities.ExchangeRate item)
{
    var key = new RateKey(
        item.Source!.Value,
        item.Frequency!.Value,
        item.CurrencyId!.Value,
        item.Date!.Value);

    var newRate = item.Rate!.Value;
    var wasUpdated = false;

    _rateCache.AddOrUpdate(
        key,
        newRate, // Add value if key does not exist
        (_, existingRate) => // Update function if key exists
        {
            if (decimal.Round(newRate, Entities.ExchangeRate.Precision) !=
                decimal.Round(existingRate, Entities.ExchangeRate.Precision))
            {
                _logger?.LogInformation(
                    "Rate correction: {Source} {Frequency} {Currency} {Date:yyyy-MM-dd} " +
                    "{OldRate} -> {NewRate}",
                    key.Source, key.Frequency, key.Currency, key.Date, existingRate, newRate);
                wasUpdated = true;
                return newRate;
            }
            return existingRate;
        });

    return wasUpdated || !_rateCache.ContainsKey(key);
}
```

**Simplified lookup:**
```csharp
private decimal? TryGetCachedRate(ExchangeRateSources source, ExchangeRateFrequencies frequency,
    CurrencyTypes currency, DateTime date, DateTime minDate)
{
    // Look backwards for the rate (handling weekends/holidays)
    for (var d = date; d >= minDate; d = d.AddDays(-1))
    {
        var key = new RateKey(source, frequency, currency, d);
        if (_rateCache.TryGetValue(key, out var rate))
            return rate;
    }
    return null;
}
```

---

### Phase 4: Extract Rate Calculator (Optional)

**Goal:** Separate rate calculation logic for better testability.

**New class:**
```csharp
public class RateCalculator
{
    private readonly Dictionary<CurrencyTypes, PeggedCurrency> _peggedCurrencies;

    public RateCalculator(IEnumerable<PeggedCurrency> peggedCurrencies)
    {
        _peggedCurrencies = peggedCurrencies.ToDictionary(p => p.CurrencyId!.Value);
    }

    public decimal? CalculateRate(
        decimal? storedRate,
        CurrencyTypes fromCurrency,
        CurrencyTypes toCurrency,
        IExchangeRateProvider provider)
    {
        if (storedRate == null) return null;
        if (fromCurrency == toCurrency) return 1m;

        return provider.QuoteType switch
        {
            QuoteTypes.Direct when toCurrency == provider.Currency => storedRate,
            QuoteTypes.Direct when fromCurrency == provider.Currency => 1 / storedRate,
            QuoteTypes.Indirect when fromCurrency == provider.Currency => storedRate,
            QuoteTypes.Indirect when toCurrency == provider.Currency => 1 / storedRate,
            _ => throw new InvalidOperationException("Unsupported QuoteType")
        };
    }

    public bool TryGetPeggedCurrency(CurrencyTypes currency, out PeggedCurrency pegged)
    {
        return _peggedCurrencies.TryGetValue(currency, out pegged);
    }
}
```

---

## Interface Changes

### IExchangeRateRepository (Simplified)

```csharp
public interface IExchangeRateRepository
{
    /// <summary>
    /// Gets exchange rate, automatically fetching from provider if not cached.
    /// Returns null if rate is unavailable.
    /// </summary>
    decimal? GetRate(CurrencyTypes fromCurrency, CurrencyTypes toCurrency,
        DateTime date, ExchangeRateSources source, ExchangeRateFrequencies frequency);

    /// <summary>
    /// String currency code overload for API convenience.
    /// </summary>
    decimal? GetRate(string fromCurrencyCode, string toCurrencyCode,
        DateTime date, ExchangeRateSources source, ExchangeRateFrequencies frequency);
}
```

**Methods to remove (move to background job):**
- `UpdateRates()` - Background job responsibility
- `EnsureMinimumDateRange()` - Now internal to GetRate

---

## Test Compatibility

All 35+ existing integration tests will continue to pass because:

1. **API contract unchanged**: `GET /api/rates` returns same response format
2. **Behavior preserved**:
   - Cross-currency calculation works
   - Pegged currency logic works
   - Date fallback works
   - Quote type conversion works
3. **Tests use WireMock**: Internal implementation changes are invisible to tests

### New Test Scenarios to Consider

```csharp
[Fact]
public async Task GetRate_WhenRateCorrected_ReturnsNewRate()
{
    // Verify rate corrections work without exceptions
}

[Fact]
public async Task GetRate_WhenMonthPartiallyLoaded_FetchesEntireMonth()
{
    // Verify single API call for entire month
}
```

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Breaking existing tests | Low | High | Run tests after each change |
| Thread safety issues | Medium | Medium | Use ConcurrentDictionary |
| Performance regression | Low | Medium | Benchmark before/after |
| Data corruption | Low | High | Audit logging for all rate changes |

---

## Implementation Order

| Step | Description | Risk | Effort |
|------|-------------|------|--------|
| 1 | Enable rate corrections (upsert) | Low | 30 min |
| 2 | Add month tracking | Low | 20 min |
| 3 | Implement month-based fetch | Medium | 40 min |
| 4 | Simplify to ConcurrentDictionary | Medium | 45 min |
| 5 | Extract RateCalculator | Low | 30 min |
| 6 | Remove unused interface methods | Low | 15 min |

**Total estimated effort:** 3 hours

---

## Success Criteria

1. All 35+ existing tests pass
2. Rate corrections accepted without exceptions
3. Missing rates trigger month-based fetch (verified via WireMock)
4. Code is simpler and more maintainable
5. No breaking changes to API contract

---

## Appendix: Files to Modify

| File | Changes |
|------|---------|
| `ExchangeRateRepository.cs` | Upsert logic, month tracking, simplified GetRate |
| `Program.cs` (InMemoryExchangeRateDataStore) | Upsert in SaveExchangeRatesAsync |
| `IExchangeRateRepository.cs` | Remove UpdateRates, EnsureMinimumDateRange |
| `IExchangeRateDataStore.cs` | Optional: Add UpsertRatesAsync |

---

*Plan created using Claude Code (Opus 4.5) - February 4, 2026*
