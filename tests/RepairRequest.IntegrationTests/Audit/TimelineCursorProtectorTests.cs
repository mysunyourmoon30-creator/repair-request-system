using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RepairRequest.Application.Audit;
using RepairRequest.IntegrationTests.Authentication;

namespace RepairRequest.IntegrationTests.Audit;

/// <summary>
/// The real HMAC-SHA256 timeline cursor (docs/15 §5) resolved from the real Infrastructure composition: round trip, byte-region
/// tamper, tenant / entity-type / Work Order binding, key separation (HKDF purpose, different signing key), and the
/// version / ticks / audit-id checks that only an authentic payload reaches (those cases are built with the derived key).
/// </summary>
public sealed class TimelineCursorProtectorTests : IAsyncLifetime
{
    private readonly AuthenticationTestHost _host = new();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _workOrder = Guid.NewGuid();
    private const string Type = "WORK_ORDER";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static ITimelineCursorProtector ProtectorOf(AuthenticationTestHost host)
    {
        using var scope = host.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ITimelineCursorProtector>();
    }

    private ITimelineCursorProtector Protector => ProtectorOf(_host);

    private static TimelinePosition Position() => new(new DateTime(2026, 10, 1, 12, 34, 56, 789, DateTimeKind.Utc), Guid.NewGuid());

    private static byte[] Decode(string cursor) => Base64Url.DecodeFromChars(cursor);

    // Same derivation as production: HKDF-SHA256 over the Jwt signing key with the fixed purpose string.
    private static byte[] DerivedKey(string signingKey, string purpose = "rr:audit-timeline-cursor:v1") =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Convert.FromBase64String(signingKey), 32, salt: [], info: Encoding.UTF8.GetBytes(purpose));

    private static byte[] Sign(byte[] key, Guid tenant, string type, Guid workOrder, byte[] payload)
    {
        var context = Encoding.UTF8.GetBytes($"AUD-TL-v1|{tenant:D}|{type}|{workOrder:D}");
        byte[] message = [.. context, .. payload];
        return HMACSHA256.HashData(key, message);
    }

    private static byte[] Payload(byte version, long ticks, Guid id)
    {
        var payload = new byte[TimelineCursorFormat.PayloadLength];
        payload[0] = version;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1, 8), ticks);
        id.TryWriteBytes(payload.AsSpan(9, 16));
        return payload;
    }

    // ------------------------------------------------------------------ format and round trip

    [Fact]
    public void Protect_ProducesA76CharacterUrlSafeCursorWithoutPadding_AndRoundTripsTheExactPosition()
    {
        var position = Position();

        var cursor = Protector.Protect(_tenant, Type, _workOrder, position);

        Assert.Equal(76, cursor.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", cursor);
        Assert.DoesNotContain('=', cursor);
        Assert.True(TimelineCursorSyntax.TryDecode(cursor, out var bytes));
        Assert.Equal(TimelineCursorFormat.Version, bytes[0]);
        Assert.True(Protector.TryUnprotect(_tenant, Type, _workOrder, bytes, out var read));
        Assert.Equal(position.OccurredAt.Ticks, read.OccurredAt.Ticks);
        Assert.Equal(DateTimeKind.Utc, read.OccurredAt.Kind);
        Assert.Equal(position.AuditId, read.AuditId);
    }

    [Fact]
    public void TheCursor_DoesNotContainTheTenantOrWorkOrderIdentifiers()
    {
        var cursor = Protector.Protect(_tenant, Type, _workOrder, Position());
        var bytes = Decode(cursor);

        Assert.DoesNotContain(_tenant.ToString("N"), cursor, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Search(bytes, _tenant.ToByteArray()));
        Assert.Empty(Search(bytes, _workOrder.ToByteArray()));
    }

    private static List<int> Search(byte[] haystack, byte[] needle)
    {
        var hits = new List<int>();
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                hits.Add(i);
            }
        }

        return hits;
    }

    [Fact]
    public void TheSamePositionAndBinding_AlwaysYieldsTheSameCursor_AndADifferentBindingADifferentTag()
    {
        var position = Position();

        var a = Protector.Protect(_tenant, Type, _workOrder, position);
        var b = Protector.Protect(_tenant, Type, _workOrder, position);
        var otherWorkOrder = Protector.Protect(_tenant, Type, Guid.NewGuid(), position);

        Assert.Equal(a, b);
        Assert.NotEqual(a, otherWorkOrder);
        Assert.NotEqual(Decode(a)[25..], Decode(otherWorkOrder)[25..]);
    }

    // ------------------------------------------------------------------ tamper

    [Theory]
    [InlineData(0, "version byte")]
    [InlineData(1, "ticks, first byte")]
    [InlineData(8, "ticks, last byte")]
    [InlineData(9, "audit id, first byte")]
    [InlineData(24, "audit id, last byte")]
    [InlineData(25, "tag, first byte")]
    [InlineData(56, "tag, last byte")]
    public void FlippingAnyByteRegion_IsRejected(int index, string region)
    {
        var bytes = Decode(Protector.Protect(_tenant, Type, _workOrder, Position()));
        bytes[index] ^= 0x01;

        Assert.False(Protector.TryUnprotect(_tenant, Type, _workOrder, bytes, out _), region);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(56)]
    [InlineData(58)]
    public void AWrongLength_IsRejected(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);

        Assert.False(Protector.TryUnprotect(_tenant, Type, _workOrder, bytes, out _));
    }

    [Fact]
    public void ARandomForgedCursor_IsRejected()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.False(Protector.TryUnprotect(_tenant, Type, _workOrder, RandomNumberGenerator.GetBytes(TimelineCursorFormat.TotalLength), out _));
        }
    }

    // ------------------------------------------------------------------ binding

    [Fact]
    public void ACursor_IsBoundToTheTenant_TheNormalizedEntityType_AndTheWorkOrder()
    {
        var bytes = Decode(Protector.Protect(_tenant, Type, _workOrder, Position()));

        Assert.True(Protector.TryUnprotect(_tenant, Type, _workOrder, bytes, out _));
        Assert.False(Protector.TryUnprotect(Guid.NewGuid(), Type, _workOrder, bytes, out _));       // another tenant
        Assert.False(Protector.TryUnprotect(_tenant, "SERVICE_VISIT", _workOrder, bytes, out _));    // another entity type
        Assert.False(Protector.TryUnprotect(_tenant, "work_order", _workOrder, bytes, out _));       // the binding uses the NORMALIZED type, not the raw route text
        Assert.False(Protector.TryUnprotect(_tenant, Type, Guid.NewGuid(), bytes, out _));           // another Work Order
    }

    // ------------------------------------------------------------------ key separation

    [Fact]
    public async Task ACursorFromAnotherSigningKey_IsRejected()
    {
        await using var otherHost = new AuthenticationTestHost();
        var position = Position();
        var foreign = Decode(ProtectorOf(otherHost).Protect(_tenant, Type, _workOrder, position));

        Assert.NotEqual(_host.SigningKey, otherHost.SigningKey);
        Assert.False(Protector.TryUnprotect(_tenant, Type, _workOrder, foreign, out _));
    }

    [Fact]
    public void TheKeyIsDerivedWithHkdf_ATagMadeWithTheRawJwtKeyOrAnotherPurposeIsRejected()
    {
        var payload = Payload(TimelineCursorFormat.Version, DateTime.UtcNow.Ticks, Guid.NewGuid());
        byte[] Cursor(byte[] key) => [.. payload, .. Sign(key, _tenant, Type, _workOrder, payload)];

        Assert.True(Protector.TryUnprotect(_tenant, Type, _workOrder, Cursor(DerivedKey(_host.SigningKey)), out _));                       // control: correct derivation
        Assert.False(Protector.TryUnprotect(_tenant, Type, _workOrder, Cursor(Convert.FromBase64String(_host.SigningKey)), out _));        // raw token-signing key
        Assert.False(Protector.TryUnprotect(_tenant, Type, _workOrder, Cursor(DerivedKey(_host.SigningKey, "rr:some-other-purpose")), out _)); // different purpose
    }

    // ------------------------------------------------------------------ authentic-payload checks (built with the derived key)

    [Fact]
    public void AnAuthenticPayload_WithAnUnknownVersion_IsRejected()
    {
        var payload = Payload(0x02, DateTime.UtcNow.Ticks, Guid.NewGuid());
        var cursor = (byte[])[.. payload, .. Sign(DerivedKey(_host.SigningKey), _tenant, Type, _workOrder, payload)];

        Assert.False(Protector.TryUnprotect(_tenant, Type, _workOrder, cursor, out _));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(3_155_378_976_000_000_000L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void AnAuthenticPayload_WithTicksOutsideTheDateTimeRange_IsRejected(long ticks)
    {
        var payload = Payload(TimelineCursorFormat.Version, ticks, Guid.NewGuid());
        var cursor = (byte[])[.. payload, .. Sign(DerivedKey(_host.SigningKey), _tenant, Type, _workOrder, payload)];

        Assert.False(Protector.TryUnprotect(_tenant, Type, _workOrder, cursor, out _));
    }

    [Fact]
    public void AnAuthenticPayload_WithAnEmptyAuditId_IsRejected()
    {
        var payload = Payload(TimelineCursorFormat.Version, DateTime.UtcNow.Ticks, Guid.Empty);
        var cursor = (byte[])[.. payload, .. Sign(DerivedKey(_host.SigningKey), _tenant, Type, _workOrder, payload)];

        Assert.False(Protector.TryUnprotect(_tenant, Type, _workOrder, cursor, out _));
    }

    [Fact]
    public void BoundaryTicks_RoundTrip()
    {
        foreach (var ticks in new[] { 0L, DateTime.MaxValue.Ticks })
        {
            var position = new TimelinePosition(new DateTime(ticks, DateTimeKind.Utc), Guid.NewGuid());
            var bytes = Decode(Protector.Protect(_tenant, Type, _workOrder, position));

            Assert.True(Protector.TryUnprotect(_tenant, Type, _workOrder, bytes, out var read));
            Assert.Equal(ticks, read.OccurredAt.Ticks);
        }
    }
}
