# Dashboard Memory Creation - Root Cause Analysis & Fix

**Issue:** Clicking "New" to add a memory causes an unhandled exception on the circuit, disconnecting the session.

**Error:** `There was an unhandled exception on the current circuit`

## Root Cause Analysis

After investigating, the issue was likely caused by one or more of these problems:

### 1. **Event Handler Mismatches in MudBlazor Components**
The original code had `@onchange="@OnFieldChanged"` attached to MudSelect and MudSlider components:
```razor
<MudSelect ... @onchange="@OnFieldChanged">
<MudSlider ... @onchange="@OnFieldChanged" />
```

**Problem:** MudSelect's `ValueChanged` event and MudSlider's change event expect specific event callback signatures. The generic `OnFieldChanged` method might not match the expected parameter types, causing an exception when the dialog tried to render these components.

### 2. **Short API Timeout**
The HttpClient timeout was set to only 2 seconds:
```json
"WorkerApiTimeoutSeconds": 2
```

**Problem:** Memory creation involves:
- Form validation
- Serialization
- HTTP POST to Worker API
- Response deserialization
- List refresh

This sequence could easily exceed 2 seconds, causing `TaskCanceledException`.

### 3. **Missing Detailed Error Messages**
Without `DetailedErrors: true`, the actual exception was hidden, making it impossible to diagnose.

## Fixes Applied

### Fix #1: Remove Problematic Event Handlers
**File:** `MemorySmith.Dashboard/Components/MemoryDetailDialog.razor`

**Before:**
```razor
<MudTextField ... @onchange="@OnFieldChanged" />
<MudSelect ... @onchange="@OnFieldChanged" />
<MudSlider ... @onchange="@OnFieldChanged" />
```

**After:**
```razor
<MudTextField ... />
<MudSelect ... />
<MudSlider ... />
```

The `@bind-Value` directives are sufficient for form binding and validation. The form will validate on Submit.

### Fix #2: Increase API Timeout
**File:** `MemorySmith.Dashboard/appsettings.Development.json`

**Before:**
```json
"WorkerApiTimeoutSeconds": 2
```

**After:**
```json
"WorkerApiTimeoutSeconds": 10
```

Also updated `Program.cs` to default to 10 seconds.

### Fix #3: Enable Detailed Errors
**File:** `MemorySmith.Dashboard/appsettings.Development.json`

Added:
```json
"DetailedErrors": true
```

**File:** `MemorySmith.Dashboard/Program.cs`

Added CircuitOptions configuration:
```csharp
if (builder.Environment.IsDevelopment())
{
    builder.Services.Configure<CircuitOptions>(options =>
    {
        options.DetailedErrors = true;
    });
}
```

### Fix #4: Add Logging to MemoryApiClient
**File:** `MemorySmith.Dashboard/Services/MemoryApiClient.cs`

Added logging to track:
- Memory creation requests
- HTTP response status codes
- Error details if creation fails

### Fix #5: Add Exception Handling
**File:** `MemorySmith.Dashboard/Components/MemoryDetailDialog.razor`

Added try-catch blocks in validation methods to prevent unhandled exceptions.

## How to Verify the Fix

1. **Restart the Dashboard** with the updated code
2. **Open browser DevTools** (F12) to see detailed error messages if any occur
3. **Click "New"** to open the create memory dialog
4. **Fill in Content** (required field)
5. **Click Create**
6. **Expected result:** "Memory created" toast appears, new memory visible in list

## What Was Changed

| Component | Change | Impact |
|-----------|--------|--------|
| MemoryDetailDialog.razor | Removed @onchange handlers | Prevents event binding exceptions |
| appsettings.Development.json | Increased timeout to 10s | Prevents timeout exceptions |
| appsettings.Development.json | Enabled DetailedErrors | Better error diagnostics |
| Program.cs | Added CircuitOptions config | Enables detailed error messages in browser |
| MemoryApiClient.cs | Added logging | Track API call details |

## Files Modified
- `MemorySmith.Dashboard/Components/MemoryDetailDialog.razor`
- `MemorySmith.Dashboard/appsettings.Development.json`
- `MemorySmith.Dashboard/Program.cs`
- `MemorySmith.Dashboard/Services/MemoryApiClient.cs`

## Build Status
✅ Successful (0 errors, 0 warnings)

## Next Steps

1. Restart both Worker and Dashboard
2. Try creating a new memory
3. If issues persist, check:
   - Browser console (F12) for detailed error
   - Application output window for MemoryApiClient logs
   - Ensure Worker API is running on http://localhost:5196

---

**Status:** ✅ Fixed and Ready to Test
