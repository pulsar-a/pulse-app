// Tests share process-wide state (the temperature unit in SensorFormat, the log path),
// so they run one at a time. The whole suite takes well under a second anyway.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
