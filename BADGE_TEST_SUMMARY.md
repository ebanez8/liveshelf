# Badge State Test Summary

## Overview

Added smoke test coverage for Live Shelf badge state mapping. The tests exercise the agent session statuses that drive card badges and verify the resulting display kind, label, and notification behavior.

## Coverage

| Scenario | Expected Badge |
|---|---|
| Working session | Running |
| Direct changed state | Changed |
| Done with no changes | Done |
| Done with changed files | DoneNeedsReview |
| Waiting for input | WaitingForApproval |
| Failed session | Failed |
| Editing files | EditingFiles |
| Running command | RunningCommand |

## Verification

The badge checks are wired into `tests/LiveShelf.SmokeTests/Program.cs` and now fail if no badge update is emitted for an expected transition.

Run them with:

```powershell
dotnet run --configuration Release --project tests\LiveShelf.SmokeTests\LiveShelf.SmokeTests.csproj
```

