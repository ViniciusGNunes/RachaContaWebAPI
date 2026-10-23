using System.Net;
using RachaPix.Api.Contracts;
using RachaPix.Api.Domain;

namespace RachaPix.Tests;

public class ExpenseConfirmationIntegrationTests : IClassFixture<RachaPixAppFactory>
{
    private readonly RachaPixAppFactory _factory;

    public ExpenseConfirmationIntegrationTests(RachaPixAppFactory factory)
    {
        _factory = factory;
    }

    private async Task<(GroupDto Group, MemberDto Alice, MemberDto Bob, string AliceToken, string BobToken)> SetupAsync(HttpClient client)
    {
        var groupRes = await client.PostAsJsonAsync("/api/groups", new CreateGroupRequest(
            "Confirmacao",
            null,
            [
                new MemberRequest("Alice", null, null),
                new MemberRequest("Bob", null, null),
            ]));
        var group = (await groupRes.Content.ReadFromJsonAsync<GroupDto>())!;
        var alice = group.Members.Single(m => m.Name == "Alice");
        var bob = group.Members.Single(m => m.Name == "Bob");

        client.SetGroupToken(group.AccessToken);
        var aliceClaim = await client.PostAsJsonAsync($"/api/groups/{group.Id}/claim", new ClaimRequest(alice.Id, null));
        var aliceToken = (await aliceClaim.Content.ReadFromJsonAsync<ClaimResultDto>())!.MemberToken;
        var bobClaim = await client.PostAsJsonAsync($"/api/groups/{group.Id}/claim", new ClaimRequest(bob.Id, null));
        var bobToken = (await bobClaim.Content.ReadFromJsonAsync<ClaimResultDto>())!.MemberToken;
        return (group, alice, bob, aliceToken, bobToken);
    }

    private static ExpenseRequest EqualExpense(MemberDto alice, MemberDto bob) => new(
        Description: "Mercado",
        Amount: 12.34m,
        Date: DateOnly.FromDateTime(DateTime.UtcNow),
        Category: ExpenseCategory.Groceries,
        PaymentMethod: PaymentMethod.Pix,
        PaidByMemberId: alice.Id,
        ShareMode: ShareMode.Equal,
        Shares:
        [
            new ExpenseShareRequest(alice.Id, null, null),
            new ExpenseShareRequest(bob.Id, null, null),
        ],
        Notes: null);

    [Fact]
    public async Task Participant_can_confirm_then_contest_an_expense()
    {
        var client = _factory.CreateClient();
        var (group, alice, bob, aliceToken, bobToken) = await SetupAsync(client);

        client.SetGroupToken(aliceToken);
        var created = await client.PostAsJsonAsync($"/api/groups/{group.Id}/expenses", EqualExpense(alice, bob));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var expense = (await created.Content.ReadFromJsonAsync<ExpenseDto>())!;

        client.SetGroupToken(bobToken);
        var confirm = await client.PostAsJsonAsync(
            $"/api/groups/{group.Id}/expenses/{expense.Id}/confirmation",
            new ConfirmationRequest(ConfirmationStatus.Confirmed, null));
        var body = await confirm.Content.ReadAsStringAsync();
        Assert.True(confirm.StatusCode == HttpStatusCode.OK, body);

        var contest = await client.PostAsJsonAsync(
            $"/api/groups/{group.Id}/expenses/{expense.Id}/confirmation",
            new ConfirmationRequest(ConfirmationStatus.Contested, "Valor errado"));
        Assert.Equal(HttpStatusCode.OK, contest.StatusCode);
    }
}
