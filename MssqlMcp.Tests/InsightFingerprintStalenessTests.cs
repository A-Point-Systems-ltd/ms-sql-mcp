// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Mssql.McpServer.InsightsLayer;

namespace MssqlMcp.Tests;

public sealed class InsightFingerprintStalenessTests
{
    private const string Fingerprint = "a1b2c3";
    private static readonly DateTime LiveModifyDate = new(2026, 8, 2, 15, 29, 49, 437);

    [Fact]
    public void Matching_fingerprint_survives_datetime_rounding_of_modify_date()
    {
        // A DATETIME2 round trip of a datetime value can come back up to ~3.3ms away.
        var storedModifyDate = LiveModifyDate.AddTicks(-33333);

        Assert.False(InsightsLayerService.IsStaleAgainstLive(
            storedObjectId: 42,
            storedModifyDate: storedModifyDate,
            storedFingerprint: Fingerprint,
            liveObjectId: 42,
            liveModifyDate: LiveModifyDate,
            liveFingerprint: Fingerprint));
    }

    [Fact]
    public void Matching_fingerprint_ignores_unrelated_modify_date_bump()
    {
        Assert.False(InsightsLayerService.IsStaleAgainstLive(
            storedObjectId: 42,
            storedModifyDate: LiveModifyDate.AddDays(-3),
            storedFingerprint: Fingerprint,
            liveObjectId: 42,
            liveModifyDate: LiveModifyDate,
            liveFingerprint: Fingerprint));
    }

    [Fact]
    public void Changed_fingerprint_is_stale()
    {
        Assert.True(InsightsLayerService.IsStaleAgainstLive(
            storedObjectId: 42,
            storedModifyDate: LiveModifyDate,
            storedFingerprint: Fingerprint,
            liveObjectId: 42,
            liveModifyDate: LiveModifyDate,
            liveFingerprint: "different"));
    }

    [Fact]
    public void Fingerprint_comparison_is_case_insensitive()
    {
        Assert.False(InsightsLayerService.IsStaleAgainstLive(
            storedObjectId: 42,
            storedModifyDate: LiveModifyDate,
            storedFingerprint: "A1B2C3",
            liveObjectId: 42,
            liveModifyDate: LiveModifyDate,
            liveFingerprint: Fingerprint));
    }

    [Fact]
    public void Recreated_object_id_is_stale_even_with_matching_fingerprint()
    {
        Assert.True(InsightsLayerService.IsStaleAgainstLive(
            storedObjectId: 42,
            storedModifyDate: LiveModifyDate,
            storedFingerprint: Fingerprint,
            liveObjectId: 99,
            liveModifyDate: LiveModifyDate,
            liveFingerprint: Fingerprint));
    }

    [Theory]
    [InlineData(-33333)]   // ~3.3ms: datetime rounding
    [InlineData(90000)]    // 9ms: still inside tolerance
    public void Without_fingerprints_small_modify_date_drift_is_not_stale(long ticksOffset)
    {
        Assert.False(InsightsLayerService.IsStaleAgainstLive(
            storedObjectId: 42,
            storedModifyDate: LiveModifyDate.AddTicks(ticksOffset),
            storedFingerprint: null,
            liveObjectId: 42,
            liveModifyDate: LiveModifyDate,
            liveFingerprint: null));
    }

    [Fact]
    public void Without_fingerprints_real_modify_date_change_is_stale()
    {
        Assert.True(InsightsLayerService.IsStaleAgainstLive(
            storedObjectId: 42,
            storedModifyDate: LiveModifyDate.AddMinutes(-1),
            storedFingerprint: null,
            liveObjectId: 42,
            liveModifyDate: LiveModifyDate,
            liveFingerprint: null));
    }

    [Fact]
    public void Missing_signals_are_not_treated_as_change()
    {
        Assert.False(InsightsLayerService.IsStaleAgainstLive(
            storedObjectId: null,
            storedModifyDate: null,
            storedFingerprint: null,
            liveObjectId: 42,
            liveModifyDate: LiveModifyDate,
            liveFingerprint: Fingerprint));
    }
}
