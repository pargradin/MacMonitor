namespace MacMonitor.Core.Models;

/// <summary>
/// One day's Anthropic API spend, used for the Cost page's 7-day mini-chart.
/// <see cref="DayUtc"/> is the start-of-UTC-day for that bucket.
/// </summary>
public sealed record DailySpend(DateTimeOffset DayUtc, decimal SpentUsd);
