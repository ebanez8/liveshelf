using LiveShelf;

var failures = new List<string>();
var estimator = new MediaTimelineEstimator();
var duration = TimeSpan.FromMinutes(5);
var timelineAnchor = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);

var firstPlaying = estimator.Resolve(
    "video",
    TimeSpan.FromSeconds(20),
    duration,
    isPlaying: true,
    timelineAnchor,
    canControlPlayback: true);
AssertBetween("playing anchor should interpolate from LastUpdatedTime", firstPlaying, 29, 31);

Thread.Sleep(1100);
var stillPlaying = estimator.Resolve(
    "video",
    TimeSpan.FromSeconds(20),
    duration,
    isPlaying: true,
    timelineAnchor,
    canControlPlayback: true);
AssertTrue("playing timeline should keep advancing past the old 4 second cap", stillPlaying > firstPlaying + TimeSpan.FromMilliseconds(800));

var paused = estimator.Resolve(
    "video",
    TimeSpan.FromSeconds(25),
    duration,
    isPlaying: false,
    DateTimeOffset.UtcNow,
    canControlPlayback: true);
AssertBetween("pause should use reported position", paused, 24.9, 25.1);

Thread.Sleep(1100);
var stillPaused = estimator.Resolve(
    "video",
    TimeSpan.FromSeconds(25),
    duration,
    isPlaying: false,
    DateTimeOffset.UtcNow,
    canControlPlayback: true);
AssertBetween("paused timeline must not drift", stillPaused, 24.9, 25.1);

var resumedImmediately = estimator.Resolve(
    "video",
    TimeSpan.FromSeconds(25),
    duration,
    isPlaying: true,
    DateTimeOffset.UtcNow,
    canControlPlayback: true);
AssertBetween("resume should not jump before confirmation", resumedImmediately, 24.9, 25.2);

Thread.Sleep(1000);
var resumedAfterConfirmation = estimator.Resolve(
    "video",
    TimeSpan.FromSeconds(25),
    duration,
    isPlaying: true,
    DateTimeOffset.UtcNow,
    canControlPlayback: true);
AssertBetween("confirmation tick should re-anchor without jumping", resumedAfterConfirmation, 24.9, 25.2);

Thread.Sleep(1100);
var resumedAdvancing = estimator.Resolve(
    "video",
    TimeSpan.FromSeconds(25),
    duration,
    isPlaying: true,
    DateTimeOffset.UtcNow,
    canControlPlayback: true);
AssertTrue("confirmed resumed timeline should advance", resumedAdvancing > TimeSpan.FromSeconds(25.8));

var seekedBackward = estimator.Resolve(
    "video",
    TimeSpan.FromSeconds(3),
    duration,
    isPlaying: true,
    DateTimeOffset.UtcNow,
    canControlPlayback: true);
AssertBetween("backward seek should reset old interpolation anchor", seekedBackward, 2.9, 3.2);

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure);
    }

    return 1;
}

Console.WriteLine("Media timeline smoke tests passed.");
return 0;

void AssertTrue(string name, bool condition)
{
    if (!condition)
    {
        failures.Add(name);
    }
}

void AssertBetween(string name, TimeSpan actual, double minSeconds, double maxSeconds)
{
    var seconds = actual.TotalSeconds;
    if (seconds < minSeconds || seconds > maxSeconds)
    {
        failures.Add($"{name}: expected {minSeconds:0.0}-{maxSeconds:0.0}s, got {seconds:0.000}s");
    }
}
