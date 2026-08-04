# Nexus Device Test Suite

Unity build target for 24-hour soak testing and chaos engineering on real devices.

## Projects

### `SoakRunner` — 24h Continuous Gameplay Loop
- Gameplay simulation (enemy kills, coin collection, level completion)
- Periodic ad requests (interstitial + rewarded)
- Auto-save every 10 minutes
- Metrics logging every minute (CSV)
- Optional chaos events (network toggle, background/foreground, time change, forced GC)

### `ChaosRunner` — Targeted Failure Scenarios
- Network loss/recovery
- Background/foreground transitions
- System time manipulation
- Memory pressure + GC stress
- Crash recovery (EncryptedStorage survival)

## Build Instructions

### Prerequisites
- Unity 6000.5.6f1 or later
- Android Build Support / iOS Build Support
- `com.nexus.core` package installed (UPM Git URL)

### Android Build
```bash
# Via Unity CLI
/Applications/Unity/Hub/Editor/6000.5.6f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath tools/nexus-device-test \
  -buildTarget Android \
  -executeMethod Nexus.DeviceTest.BuildScript.BuildAndroid \
  -outputPath ../builds/nexus-soak-android.apk \
  -quit
```

### iOS Build
```bash
/Applications/Unity/Hub/Editor/6000.5.6f1/Unity.app/Contents/MacOS/Unity \
  -batchmode \
  -projectPath tools/nexus-device-test \
  -buildTarget iOS \
  -executeMethod Nexus.DeviceTest.BuildScript.BuildIOS \
  -outputPath ../builds/nexus-soak-ios \
  -quit
```

## Running Tests

### Soak Test (24h)
1. Install APK/IPA on device
2. Launch app → `SoakRunner` starts automatically
3. Monitor logcat / Xcode console for metrics
4. After 24h, results saved to:
   - `persistentDataPath/soak_log_<timestamp>.csv`
   - `persistentDataPath/soak_log_<timestamp>_summary.txt`

### Chaos Test
1. Install APK/IPA on device
2. Launch app → Tap "Run Chaos Tests" button (or auto-run on start)
3. Results saved to:
   - `persistentDataPath/chaos_report_<timestamp>.json`

## Metrics Collected

### Soak Metrics (CSV)
| Column | Description |
|--------|-------------|
| Timestamp | ISO 8601 timestamp |
| ElapsedMinutes | Hours since start |
| LoopCount | Gameplay iterations |
| AdRequests | Total ad requests |
| Saves | Auto-save count |
| ChaosEvents | Chaos triggers fired |
| MemoryMB | Current managed memory |
| PeakMemoryMB | Peak memory observed |
| GCGen0/1/2 | GC collection counts |

### Chaos Report (JSON)
```json
{
  "Timestamp": "2026-08-04T12:00:00Z",
  "TotalScenarios": 50,
  "Passed": 48,
  "Failed": 2,
  "Results": [
    {
      "Scenario": "NetworkLoss",
      "Iteration": 0,
      "Success": true,
      "DurationMs": 45,
      "Error": null,
      "Timestamp": "2026-08-04T12:00:01Z"
    }
  ]
}
```

## CI Integration

### GitHub Actions (Self-hosted runner with device)
```yaml
# .github/workflows/device-soak.yml
device-soak:
  runs-on: [self-hosted, android-device]
  steps:
    - uses: actions/checkout@v4
    - name: Build Android
      run: ./build_android.sh
    - name: Install on Device
      run: adb install -r builds/nexus-soak-android.apk
    - name: Run Soak (24h)
      run: |
        adb shell am start -n com.nexus.devicetest/com.unity3d.player.UnityPlayerActivity
        sleep 86400  # 24 hours
        adb pull /sdcard/Android/data/com.nexus.devicetest/files/soak_log_*.csv artifacts/
    - name: Upload Results
      uses: actions/upload-artifact@v4
      with:
        name: soak-results
        path: artifacts/
```

## Configuration

### SoakRunner Settings
| Field | Default | Description |
|-------|---------|-------------|
| Target Duration Hours | 24 | Test duration |
| Gameplay Loop Interval | 30s | Gameplay simulation frequency |
| Ad Request Interval | 5min | Ad request frequency |
| Save Interval | 10min | Auto-save frequency |
| Metrics Log Interval | 1min | CSV logging frequency |
| Enable Chaos Events | true | Random chaos triggers |

### ChaosRunner Settings
| Field | Default | Description |
|-------|---------|-------------|
| Iterations Per Scenario | 10 | Repetitions per test |
| Delay Between Iterations | 5s | Cooldown between runs |

## Requirements

- **Device**: Android 8.0+ / iOS 13+ (ARM64)
- **Storage**: 500MB+ free (for logs)
- **Battery**: Charging recommended for 24h test
- **Network**: Intermittent connectivity for chaos tests

## Output Analysis

### Soak CSV Analysis (Python)
```python
import pandas as pd

df = pd.read_csv('soak_log_20260804_120000.csv')
print(f"Duration: {df['ElapsedMinutes'].max():.1f} min")
print(f"Peak Memory: {df['PeakMemoryMB'].max():.1f} MB")
print(f"Total Loops: {df['LoopCount'].max()}")
print(f"GC Gen2 Collections: {df['GCGen2'].max()}")
print(f"Memory Growth: {df['MemoryMB'].iloc[-1] - df['MemoryMB'].iloc[0]:.1f} MB")
```

### Chaos Report Analysis
```python
import json

with open('chaos_report_20260804_120000.json') as f:
    report = json.load(f)

print(f"Pass Rate: {report['Passed']}/{report['TotalScenarios']} ({100*report['Passed']/report['TotalScenarios']:.1f}%)")
for r in report['Results']:
    if not r['Success']:
        print(f"FAIL: {r['Scenario']} #{r['Iteration']} - {r['Error']}")
```

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Build fails: missing `com.nexus.core` | Install via UPM: `https://github.com/CevatAYDN/GameContainer.git?path=Nexus/Packages/com.nexus.core` |
| App crashes on start | Check logcat for `Nexus.Core.Root` initialization errors |
| No logs written | Verify `Application.persistentDataPath` is writable |
| Chaos tests fail | Ensure `OfflineTimeCalculator`, `EncryptedStorageService` are registered in contexts |

## License

MIT — Same as Nexus Core.