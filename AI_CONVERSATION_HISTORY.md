# AI Interaction History - Taxually Coding Challenge

**Candidate:** Simiso Mazibuko
**Date:** February 4, 2026
**AI Tool:** Claude Code (Opus 4.5 via CLI)
**Session ID:** Generated during live session

---

## Session Overview

This document captures my AI-assisted development workflow for the Taxually Exchange Rate refactoring challenge. It demonstrates:
- How I use AI to analyze and understand code
- My prompting strategy and follow-up questions
- Critical evaluation of AI suggestions
- Decision-making process

---

## Phase 1: Initial Analysis

### Prompt 1: Clone and Explore Repository

**Me:**
```
Clone the repository at https://github.com/Taxually-Engineering/ai-assisted-coding-challenge
and help me understand the codebase structure.
```

**Claude Response:**
- Cloned repository successfully
- Identified key files: ExchangeRateRepository.cs, tests, providers
- Listed project structure and dependencies

### Prompt 2: Analyze the Core Repository

**Me:**
```
Read the ExchangeRateRepository.cs file and identify the main issues
that need refactoring based on the job description requirements.
```

**Claude Response:**
Identified 5 key issues:
1. Rate corrections throw exceptions (line 370-376)
2. No month-based caching strategy
3. Monolithic 506-line class
4. Complex nested dictionary structure
5. Sync-over-async anti-pattern

**My Evaluation:**
- Issue #1 (rate corrections) is critical - directly violates business requirement
- Issue #2 (month caching) is critical - directly violates business requirement
- Issues #3-5 are valid but lower priority

---

## Phase 2: Business Requirements Mapping

### Prompt 3: Map Requirements to Code

**Me:**
```
Map the business requirements from the job posting to specific code locations.
Requirements:
1. Transactions processed serially
2. Data typically within same month - fetch/cache full month
3. External providers have rate limits - minimize HTTP requests
4. Must support rate corrections (overwrite existing rates)
```

**Claude Response:**

| Requirement | Current Code | Problem |
|-------------|--------------|---------|
| Rate corrections | Line 370-376 | Throws exception on different rate |
| Month-based fetch | UpdateRates() | Fetches by date range, not month |
| Minimize API calls | GetHistoricalDailyFxRates | Can make many calls |
| Serial processing | GetRate() | Works correctly |

**My Evaluation:**
Accurate mapping. The two critical issues are clear:
1. `AddRateToDictionaries` blocks corrections
2. No month-level caching/tracking

---

## Phase 3: Solution Design

### Prompt 4: Propose Refactoring Approach

**Me:**
```
Propose a minimal refactoring approach that:
1. Enables rate corrections
2. Implements month-based caching
3. Keeps all tests passing
4. Follows simplicity-first principle
```

**Claude Response:**
Proposed 4-phase approach:
1. Modify AddRateToDictionaries for upsert (30 min)
2. Add month tracking HashSet (20 min)
3. Implement EnsureMonthLoaded method (40 min)
4. Optional: Simplify to ConcurrentDictionary (45 min)

**My Evaluation:**
- Phase 1-3 are essential and low-risk
- Phase 4 is optional - adds thread safety but more changes
- Total: ~1.5 hours for essential changes

### Prompt 5: Challenge the Approach

**Me:**
```
What are the risks of this approach? What could break?
```

**Claude Response:**
Identified risks:
- Breaking existing tests (mitigated by incremental changes)
- Thread safety with HashSet (use lock or ConcurrentDictionary)
- Performance regression (benchmark before/after)

**My Evaluation:**
Good risk identification. Tests use WireMock and test through API, so internal changes should be safe.

---

## Phase 4: Implementation

### Prompt 6: Generate Rate Correction Fix

**Me:**
```
Show me the exact code change for AddRateToDictionaries to support upserts.
Keep it minimal.
```

**Claude Response:**
```csharp
if (datesByCurrency.TryGetValue(date, out var savedRate))
{
    var roundedNew = decimal.Round(newRate, Precision);
    var roundedSaved = decimal.Round(savedRate, Precision);

    if (roundedNew != roundedSaved)
    {
        _logger?.LogInformation("Rate correction: {Currency} {Date} {OldRate} -> {NewRate}",
            currency, date, savedRate, newRate);
        datesByCurrency[date] = newRate; // UPDATE instead of throw
        return true;
    }
    return false;
}
```

**My Evaluation:**
Clean change. Keeps precision check, adds logging for audit trail, returns true to signal persistence needed.

### Prompt 7: Generate Month Tracking

**Me:**
```
Show me the EnsureMonthLoaded implementation with the HashSet tracking.
```

**Claude Response:**
```csharp
private readonly HashSet<(ExchangeRateSources, ExchangeRateFrequencies, int, int)> _loadedMonths;

private void EnsureMonthLoaded(source, frequency, date)
{
    var monthKey = (source, frequency, date.Year, date.Month);
    if (_loadedMonths.Contains(monthKey))
        return;

    // Load from DB
    // If empty, fetch from provider
    FetchAndStoreMonth(source, frequency, monthStart);

    _loadedMonths.Add(monthKey);
}
```

**My Evaluation:**
Simple and effective. HashSet lookup is O(1). Prevents redundant API calls.

---

## Phase 5: Testing Strategy

### Prompt 8: Verify Test Compatibility

**Me:**
```
Will the existing tests still pass after these changes?
What tests should I add?
```

**Claude Response:**
- Existing tests will pass (they test through API endpoint)
- WireMock mocks external calls, internal changes invisible
- Suggested new tests:
  - GetRate_WhenRateCorrected_ReturnsNewRate
  - GetRate_WhenMonthPartiallyLoaded_FetchesEntireMonth

**My Evaluation:**
Correct analysis. The integration tests use WebApplicationFactory and WireMock, testing behavior not implementation.

---

## Phase 6: Documentation

### Prompt 9: Create Architecture Plan

**Me:**
```
Create an architecture plan document that explains:
1. Current state problems
2. Proposed solution
3. Implementation phases
4. Risk assessment
```

**Claude Response:**
Generated comprehensive TAXUALLY_EXCHANGE_RATE_REFACTORING_PLAN.md

**My Evaluation:**
Complete document covering all aspects. Ready for submission.

---

## Key Decisions Made

| Decision | Rationale | AI Suggested | I Agreed |
|----------|-----------|--------------|----------|
| Upsert over exception | Business requirement | Yes | Yes |
| Month-based tracking | Minimize API calls | Yes | Yes |
| Keep nested dict | Minimal changes | Yes | Yes |
| Skip ConcurrentDictionary | Lower priority | Yes | Yes |
| Log corrections | Audit trail | Yes | Yes |

---

## What I Learned

1. **AI is good at**: Pattern recognition, mapping requirements to code, generating boilerplate
2. **I needed to verify**: Business requirement interpretation, test compatibility
3. **I pushed back on**: Over-engineering suggestions (full async conversion)

---

## Total Time

| Phase | Time |
|-------|------|
| Analysis | 20 min |
| Design | 15 min |
| Implementation | 30 min |
| Documentation | 25 min |
| **Total** | ~90 min |

---

## Conclusion

The AI-assisted approach allowed me to:
- Quickly identify the critical issues
- Map business requirements to code
- Generate correct implementation patterns
- Avoid over-engineering

The key was maintaining critical evaluation and focusing on the actual business requirements rather than pursuing theoretical improvements.

---

*Conversation captured from Claude Code (Opus 4.5) session - February 4, 2026*
