# 2ANEND Telemetry (package)

Consent-gated playtest telemetry for Unity 6. The full write-up, install steps, event reference and analysis tools are in the [repository README](../../README.md).

```csharp
using TwoAnEnd.Playtest;

Telemetry.Log("judge", "lane", 2, "result", "good", "ms", -18.4f);
Telemetry.BeginSpan("load", "to", "Overworld");   /* ... */   Telemetry.EndSpan("load");
Telemetry.Track("overworld", player.transform);
Telemetry.AddContext("song", () => currentSong);
Telemetry.SetConsent(true, "survey");
```

- **Config:** `Assets/Resources/telemetry_config.json`. Start from `telemetry_config.example.json`, or use Tools > 2ANEND Telemetry > Create Config. Keep the file out of git.
- **Logs:** `Application.persistentDataPath/Telemetry/*.jsonl`. Open the folder from Window > 2ANEND > Telemetry.
- **In game:** F3 shows the live overlay.
- **Demo:** Tools > 2ANEND Telemetry > Create Demo Scene.
- **Benchmark:** run the build with `-benchmark -benchmarkSeconds 30`.
