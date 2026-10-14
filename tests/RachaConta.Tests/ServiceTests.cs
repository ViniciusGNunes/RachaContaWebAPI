using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using RachaConta.Api.Domain;
using RachaConta.Api.Services;

namespace RachaConta.Tests;

public class ShareCalculatorTests
{
    [Fact]
    public void Equal_distributes_leftover_cents_to_first()
    {
        var r = ShareCalculator.Equal(100.00m, 3);
        Assert.Equal([33.34m, 33.33m, 33.33m], r);
        Assert.Equal(100.00m, r.Sum());
    }

    [Theory]
    [InlineData(300.00, 3)]
    [InlineData(0.05, 7)]
    [InlineData(999999.99, 50)]
    public void Equal_sum_is_exact(double total, int n)
    {
        Assert.Equal((decimal)total, ShareCalculator.Equal((decimal)total, n).Sum());
    }

    [Fact]
    public void Percentage_remainder_goes_to_first()
    {
        var r = ShareCalculator.Percentage(100.00m, [33.33m, 33.33m, 33.34m]);
        Assert.Equal(100.00m, r.Sum());
        Assert.Equal([33.33m, 33.33m, 33.34m], r);
        var r2 = ShareCalculator.Percentage(0.10m, [50m, 50m]);
        Assert.Equal([0.05m, 0.05m], r2);
        var r3 = ShareCalculator.Percentage(10.00m, [33.333m, 33.333m, 33.334m]);
        Assert.Equal(10.00m, r3.Sum());
    }

    [Fact]
    public void Percentage_sum_validation()
    {
        Assert.True(ShareCalculator.PercentagesSumTo100([50m, 50.01m]));
        Assert.False(ShareCalculator.PercentagesSumTo100([50m, 49m]));
    }
}

public class PixTests
{
    [Fact]
    public void Crc16_known_vector() => Assert.Equal("29B1", Crc16.ComputeHex("123456789"));

    [Fact]
    public void Payload_known_good_bcb_example()
    {
        // Static Pix example (no amount) with a self-consistent CRC.
        var code = PixPayload.Build("123e4567-e12b-12d1-a456-426655440000", "Fulano de Tal", "Brasilia", null, txId: null);
        Assert.StartsWith("00020126", code);
        Assert.Contains("0014br.gov.bcb.pix0136123e4567-e12b-12d1-a456-426655440000", code);
        Assert.Contains("5303986", code);
        Assert.Contains("5913FULANO DE TAL", code);
        Assert.Contains("6008BRASILIA", code);
        Assert.Contains("62070503***", code);
        Assert.Equal(Crc16.ComputeHex(code[..^4]), code[^4..]);
    }

    [Fact]
    public void Payload_includes_amount_and_sanitizes()
    {
        var code = PixPayload.Build("fulano@email.com", "José da Conceição Ñandú Machado Longo", "São Paulo", 25.5m, "RCk3Fx9aQz1");
        Assert.Contains("540525.50", code);
        Assert.Contains("5925JOSE DA CONCEICAO NANDU M", code);
        Assert.Contains("6009SAO PAULO", code);
        Assert.Contains("0511RCk3Fx9aQz1", code);
        Assert.Equal(Crc16.ComputeHex(code[..^4]), code[^4..]);
    }

    [Theory]
    [InlineData(PixKeyType.Cpf, "529.982.247-25", "52998224725")]
    [InlineData(PixKeyType.Cnpj, "11.222.333/0001-81", "11222333000181")]
    [InlineData(PixKeyType.Email, " Fulano@Email.COM ", "fulano@email.com")]
    [InlineData(PixKeyType.Phone, "(11) 98765-4321", "+5511987654321")]
    [InlineData(PixKeyType.Phone, "+55 11 98765-4321", "+5511987654321")]
    public void Key_normalization(PixKeyType type, string raw, string expected)
    {
        Assert.True(PixKeyNormalizer.TryNormalize(type, raw, out var n, out _));
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData(PixKeyType.Cpf, "111.111.111-11")]
    [InlineData(PixKeyType.Email, "nope")]
    [InlineData(PixKeyType.Random, "abc")]
    public void Key_invalid(PixKeyType type, string raw) =>
        Assert.False(PixKeyNormalizer.TryNormalize(type, raw, out _, out _));
}

public class BalanceTests
{
    [Fact]
    public void Balances_and_transfers()
    {
        BalanceMember a = new(Guid.NewGuid(), "Ana"), b = new(Guid.NewGuid(), "Bruno"), c = new(Guid.NewGuid(), "Caio");
        var expenses = new[]
        {
            new BalanceExpense(a.MemberId, 300m, [(a.MemberId, 100m), (b.MemberId, 100m), (c.MemberId, 100m)]),
            new BalanceExpense(b.MemberId, 60m, [(a.MemberId, 20m), (b.MemberId, 20m), (c.MemberId, 20m)]),
        };
        var bal = BalanceCalculator.Compute([a, b, c], expenses, []);
        Assert.Equal([180m, -60m, -120m], bal.Select(x => x.Net));
        var t = BalanceCalculator.SuggestTransfers(bal);
        Assert.Equal(2, t.Count);
        Assert.Equal(c.MemberId, t[0].FromMemberId);
        Assert.Equal(120m, t[0].Amount);
        Assert.Equal(180m, t.Sum(x => x.Amount) );
    }

    [Fact]
    public void Settlement_reduces_debt()
    {
        BalanceMember a = new(Guid.NewGuid(), "Ana"), b = new(Guid.NewGuid(), "Bruno");
        var bal = BalanceCalculator.Compute([a, b],
            [new BalanceExpense(a.MemberId, 100m, [(a.MemberId, 50m), (b.MemberId, 50m)])],
            [new BalanceSettlement(b.MemberId, a.MemberId, 50m)]);
        Assert.All(bal, x => Assert.Equal(0m, x.Net));
        Assert.Empty(BalanceCalculator.SuggestTransfers(bal));
    }
}

public class SplitPaymentConfirmationTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection = new("DataSource=:memory:");
    private readonly RachaConta.Api.Data.AppDbContext _db;
    private readonly SplitService _service;

    public SplitPaymentConfirmationTests()
    {
        _connection.Open();
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<RachaConta.Api.Data.AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new RachaConta.Api.Data.AppDbContext(options);
        _db.Database.EnsureCreated();
        _service = new SplitService(_db, TimeProvider.System);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<(RachaConta.Api.Contracts.SplitAdminDto Split, Guid GuestId)> CreateSplitAsync()
    {
        var split = await _service.CreateAsync(new RachaConta.Api.Contracts.CreateSplitRequest(
            "Pizza", 100m, "52998224725", PixKeyType.Cpf, "Fulano", "Sao Paulo", SplitMode.Equal, true,
            [
                new("Ana", null, true),
                new("Beto", null, false),
            ]), CancellationToken.None);
        return (split, split.Participants.Single(p => !p.IsOwner).Id);
    }

    [Fact]
    public async Task Guest_mark_paid_is_pending_until_admin_confirms()
    {
        var (split, guestId) = await CreateSplitAsync();

        var publicDto = await _service.MarkPaidAsync(split.Slug, guestId, CancellationToken.None);
        var guest = publicDto.Participants.Single(p => p.Id == guestId);
        Assert.False(guest.Paid);
        Assert.True(guest.PaymentPending);
        Assert.Equal(1, publicDto.PaidCount);
        Assert.Equal(1, publicDto.PendingCount);

        var confirmed = await _service.TogglePaidAsync(split.Slug, guestId, split.AdminToken, CancellationToken.None);
        var confirmedGuest = confirmed.Participants.Single(p => p.Id == guestId);
        Assert.True(confirmedGuest.Paid);
        Assert.False(confirmedGuest.PaymentPending);
        Assert.Equal(2, confirmed.PaidCount);
        Assert.Equal(0, confirmed.PendingCount);
    }

    [Fact]
    public async Task Admin_can_reject_a_pending_claim()
    {
        var (split, guestId) = await CreateSplitAsync();
        await _service.MarkPaidAsync(split.Slug, guestId, CancellationToken.None);

        var rejected = await _service.RejectClaimAsync(split.Slug, guestId, split.AdminToken, CancellationToken.None);
        var guest = rejected.Participants.Single(p => p.Id == guestId);
        Assert.False(guest.Paid);
        Assert.False(guest.PaymentPending);
        Assert.Equal(0, rejected.PendingCount);
    }

    [Fact]
    public async Task Mark_paid_does_not_downgrade_a_confirmed_payment()
    {
        var (split, guestId) = await CreateSplitAsync();
        await _service.TogglePaidAsync(split.Slug, guestId, split.AdminToken, CancellationToken.None);

        var publicDto = await _service.MarkPaidAsync(split.Slug, guestId, CancellationToken.None);
        var guest = publicDto.Participants.Single(p => p.Id == guestId);
        Assert.True(guest.Paid);
        Assert.False(guest.PaymentPending);
    }
}

public class GroupAccessTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection = new("DataSource=:memory:");
    private readonly RachaConta.Api.Data.AppDbContext _db;
    private readonly GroupService _groups;
    private readonly IServiceProvider _services;

    public GroupAccessTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<RachaConta.Api.Data.AppDbContext>().UseSqlite(_connection).Options;
        _db = new RachaConta.Api.Data.AppDbContext(options);
        _db.Database.EnsureCreated();
        _groups = new GroupService(_db, TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new PixOptions()));
        _services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton(_db)
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<RachaConta.Api.Contracts.GroupDto> CreateGroupAsync(string name = "Viagem") =>
        await _groups.CreateAsync(new RachaConta.Api.Contracts.CreateGroupRequest(name, null,
            [new("Ana", null, null), new("Beto", null, null)]), CancellationToken.None);

    private async Task<bool> PassesAsync(Guid id, string? token, bool requireAdmin)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = _services };
        if (token is not null) http.Request.Headers["X-Group-Token"] = token;

        var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(
            http, new Microsoft.AspNetCore.Routing.RouteData(),
            new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var executing = new Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext(
            actionContext, [], new Dictionary<string, object?> { ["id"] = id }, controller: new object());

        var nextCalled = false;
        var filter = new RachaConta.Api.Controllers.GroupAccessAttribute { RequireAdmin = requireAdmin };
        try
        {
            await filter.OnActionExecutionAsync(executing, () =>
            {
                nextCalled = true;
                return Task.FromResult(new Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext(
                    actionContext, [], controller: new object()));
            });
        }
        catch (NotFoundException)
        {
            return false;
        }
        return nextCalled;
    }

    [Fact]
    public async Task Create_returns_distinct_tokens()
    {
        var group = await CreateGroupAsync();
        Assert.False(string.IsNullOrEmpty(group.AccessToken));
        Assert.False(string.IsNullOrEmpty(group.AdminToken));
        Assert.NotEqual(group.AccessToken, group.AdminToken);
    }

    [Fact]
    public async Task Access_token_grants_read_but_not_admin()
    {
        var group = await CreateGroupAsync();
        Assert.True(await PassesAsync(group.Id, group.AccessToken, requireAdmin: false));
        Assert.False(await PassesAsync(group.Id, group.AccessToken, requireAdmin: true));
    }

    [Fact]
    public async Task Admin_token_grants_everything()
    {
        var group = await CreateGroupAsync();
        Assert.True(await PassesAsync(group.Id, group.AdminToken, requireAdmin: false));
        Assert.True(await PassesAsync(group.Id, group.AdminToken, requireAdmin: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-token")]
    public async Task Missing_or_wrong_token_is_rejected(string? token)
    {
        var group = await CreateGroupAsync();
        Assert.False(await PassesAsync(group.Id, token, requireAdmin: false));
    }

    [Fact]
    public async Task Unknown_group_is_rejected()
    {
        Assert.False(await PassesAsync(Guid.NewGuid(), "anything", requireAdmin: false));
    }

    [Fact]
    public async Task Empty_stored_token_never_matches_empty_header()
    {
        var group = await CreateGroupAsync();
        var entity = await _db.Groups.FindAsync(group.Id);
        entity!.AccessToken = "";
        entity.AdminToken = "";
        await _db.SaveChangesAsync();

        Assert.False(await PassesAsync(group.Id, "", requireAdmin: false));
        Assert.False(await PassesAsync(group.Id, null, requireAdmin: false));
    }

    [Fact]
    public async Task Lookup_returns_only_authorized_groups()
    {
        var mine = await CreateGroupAsync("Minha");
        var shared = await CreateGroupAsync("Compartilhada");
        var other = await CreateGroupAsync("Alheia");

        var result = await _groups.LookupAsync(
        [
            new(mine.Id, mine.AdminToken),
            new(shared.Id, shared.AccessToken),
            new(other.Id, "wrong"),
            new(Guid.NewGuid(), "unknown"),
            null,
        ], CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.True(result.Single(g => g.Id == mine.Id).IsAdmin);
        Assert.False(result.Single(g => g.Id == shared.Id).IsAdmin);
        Assert.DoesNotContain(result, g => g.Id == other.Id);
    }
}
