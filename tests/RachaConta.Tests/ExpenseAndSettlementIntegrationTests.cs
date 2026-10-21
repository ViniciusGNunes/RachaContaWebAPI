using System.Net;
using RachaConta.Api.Contracts;
using RachaConta.Api.Domain;

namespace RachaConta.Tests;

public class ExpenseAndSettlementIntegrationTests : IClassFixture<RachaContaAppFactory>
{
    private readonly RachaContaAppFactory _factory;

    public ExpenseAndSettlementIntegrationTests(RachaContaAppFactory factory)
    {
        _factory = factory;
    }

    private async Task<(GroupDto Group, MemberDto Alice, MemberDto Bob, string AliceToken, string BobToken)> SetupGroupWithIdentitiesAsync(HttpClient client)
    {
        var groupRes = await client.PostAsJsonAsync("/api/groups", new CreateGroupRequest(
            "Apartamento 101",
            "Despesas do apê",
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

    [Fact]
    public async Task Expense_create_list_get_update_and_delete()
    {
        var client = _factory.CreateClient();
        var (group, alice, bob, aliceToken, _) = await SetupGroupWithIdentitiesAsync(client);

        client.SetGroupToken(aliceToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // 1. Create Expense
        var createReq = new ExpenseRequest(
            Description: "Jantar",
            Amount: 100.00m,
            Date: today,
            Category: ExpenseCategory.Food,
            PaymentMethod: PaymentMethod.CreditCard,
            PaidByMemberId: alice.Id,
            ShareMode: ShareMode.Equal,
            Shares:
            [
                new ExpenseShareRequest(alice.Id, null, null),
                new ExpenseShareRequest(bob.Id, null, null),
            ],
            Notes: "Restaurante italiano");

        var createRes = await client.PostAsJsonAsync($"/api/groups/{group.Id}/expenses", createReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var expense = await createRes.Content.ReadFromJsonAsync<ExpenseDto>();
        Assert.NotNull(expense);
        Assert.Equal("Jantar", expense.Description);
        Assert.Equal(100.00m, expense.Amount);
        Assert.Equal(2, expense.Shares.Count);
        Assert.Equal(50.00m, expense.Shares[0].Amount);
        Assert.Equal(50.00m, expense.Shares[1].Amount);

        // 2. Get Expense by Id
        var getRes = await client.GetAsync($"/api/groups/{group.Id}/expenses/{expense.Id}");
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        var fetched = await getRes.Content.ReadFromJsonAsync<ExpenseDto>();
        Assert.NotNull(fetched);
        Assert.Equal(expense.Id, fetched.Id);

        // 3. List Expenses with filter
        var listFoodRes = await client.GetAsync($"/api/groups/{group.Id}/expenses?category=Food");
        Assert.Equal(HttpStatusCode.OK, listFoodRes.StatusCode);
        var foodList = await listFoodRes.Content.ReadFromJsonAsync<List<ExpenseDto>>();
        Assert.NotNull(foodList);
        Assert.Single(foodList);

        var listUtilRes = await client.GetAsync($"/api/groups/{group.Id}/expenses?category=Utilities");
        Assert.Equal(HttpStatusCode.OK, listUtilRes.StatusCode);
        var utilList = await listUtilRes.Content.ReadFromJsonAsync<List<ExpenseDto>>();
        Assert.NotNull(utilList);
        Assert.Empty(utilList);

        // 4. Update Expense
        var updateReq = new ExpenseRequest(
            Description: "Jantar Especial",
            Amount: 120.00m,
            Date: today,
            Category: ExpenseCategory.Food,
            PaymentMethod: PaymentMethod.CreditCard,
            PaidByMemberId: alice.Id,
            ShareMode: ShareMode.Equal,
            Shares:
            [
                new ExpenseShareRequest(alice.Id, null, null),
                new ExpenseShareRequest(bob.Id, null, null),
            ],
            Notes: "Com sobremesa");

        var updateRes = await client.PutAsJsonAsync($"/api/groups/{group.Id}/expenses/{expense.Id}", updateReq);
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
        var updated = await updateRes.Content.ReadFromJsonAsync<ExpenseDto>();
        Assert.NotNull(updated);
        Assert.Equal("Jantar Especial", updated.Description);
        Assert.Equal(120.00m, updated.Amount);
        Assert.Equal(60.00m, updated.Shares[0].Amount);

        // 5. Delete Expense
        var deleteRes = await client.DeleteAsync($"/api/groups/{group.Id}/expenses/{expense.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);

        // Subsequent get returns 404
        var getAfterDelete = await client.GetAsync($"/api/groups/{group.Id}/expenses/{expense.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getAfterDelete.StatusCode);
    }

    [Fact]
    public async Task Settlement_lifecycle_and_balances_verification()
    {
        var client = _factory.CreateClient();
        var (group, alice, bob, aliceToken, bobToken) = await SetupGroupWithIdentitiesAsync(client);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // 1. Alice pays 100.00 for groceries split equally with Bob
        client.SetGroupToken(aliceToken);
        var expenseRes = await client.PostAsJsonAsync($"/api/groups/{group.Id}/expenses", new ExpenseRequest(
            Description: "Mercado Semanal",
            Amount: 100.00m,
            Date: today,
            Category: ExpenseCategory.Groceries,
            PaymentMethod: PaymentMethod.DebitCard,
            PaidByMemberId: alice.Id,
            ShareMode: ShareMode.Equal,
            Shares:
            [
                new ExpenseShareRequest(alice.Id, null, null),
                new ExpenseShareRequest(bob.Id, null, null),
            ],
            Notes: null));
        Assert.Equal(HttpStatusCode.Created, expenseRes.StatusCode);

        // 2. Check initial balances
        var initialBalancesRes = await client.GetAsync($"/api/groups/{group.Id}/balances");
        Assert.Equal(HttpStatusCode.OK, initialBalancesRes.StatusCode);
        var initialBalances = await initialBalancesRes.Content.ReadFromJsonAsync<BalanceDto>();
        Assert.NotNull(initialBalances);

        var aliceBal = initialBalances.Members.Single(m => m.MemberId == alice.Id);
        var bobBal = initialBalances.Members.Single(m => m.MemberId == bob.Id);

        Assert.Equal(100.00m, aliceBal.Paid);
        Assert.Equal(50.00m, aliceBal.Owed);
        Assert.Equal(50.00m, aliceBal.Net); // Alice should receive 50

        Assert.Equal(0.00m, bobBal.Paid);
        Assert.Equal(50.00m, bobBal.Owed);
        Assert.Equal(-50.00m, bobBal.Net); // Bob owes 50

        Assert.Single(initialBalances.SuggestedTransfers);
        Assert.Equal(bob.Id, initialBalances.SuggestedTransfers[0].FromMemberId);
        Assert.Equal(alice.Id, initialBalances.SuggestedTransfers[0].ToMemberId);
        Assert.Equal(50.00m, initialBalances.SuggestedTransfers[0].Amount);

        // 3. Bob creates a settlement paying Alice 50.00 (as payer, it starts as Pending)
        client.SetGroupToken(bobToken);
        var createSettlementRes = await client.PostAsJsonAsync($"/api/groups/{group.Id}/settlements", new CreateSettlementRequest(
            FromMemberId: bob.Id,
            ToMemberId: alice.Id,
            Amount: 50.00m,
            Date: today,
            PaymentMethod: PaymentMethod.Pix,
            Notes: "Pix enviado"));
        Assert.Equal(HttpStatusCode.Created, createSettlementRes.StatusCode);
        var settlement = await createSettlementRes.Content.ReadFromJsonAsync<SettlementDto>();
        Assert.NotNull(settlement);
        Assert.Equal(SettlementStatus.Pending, settlement.Status);

        // 4. List settlements as Bob
        var listSettlementsRes = await client.GetAsync($"/api/groups/{group.Id}/settlements");
        Assert.Equal(HttpStatusCode.OK, listSettlementsRes.StatusCode);
        var settlementsList = await listSettlementsRes.Content.ReadFromJsonAsync<List<SettlementDto>>();
        Assert.NotNull(settlementsList);
        Assert.Single(settlementsList);

        // Balances while pending: settlement not yet counted
        var midBalancesRes = await client.GetAsync($"/api/groups/{group.Id}/balances");
        var midBalances = await midBalancesRes.Content.ReadFromJsonAsync<BalanceDto>();
        Assert.NotNull(midBalances);
        Assert.Equal(50.00m, midBalances.Members.Single(m => m.MemberId == alice.Id).Net);

        // 5. Alice (receiver) confirms the settlement
        client.SetGroupToken(aliceToken);
        var confirmRes = await client.PostAsync($"/api/groups/{group.Id}/settlements/{settlement.Id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirmRes.StatusCode);
        var confirmedSettlement = await confirmRes.Content.ReadFromJsonAsync<SettlementDto>();
        Assert.NotNull(confirmedSettlement);
        Assert.Equal(SettlementStatus.Confirmed, confirmedSettlement.Status);

        // 6. Balances after confirmation: debt is settled, net is 0 for both!
        var settledBalancesRes = await client.GetAsync($"/api/groups/{group.Id}/balances");
        var settledBalances = await settledBalancesRes.Content.ReadFromJsonAsync<BalanceDto>();
        Assert.NotNull(settledBalances);

        var aliceFinal = settledBalances.Members.Single(m => m.MemberId == alice.Id);
        var bobFinal = settledBalances.Members.Single(m => m.MemberId == bob.Id);

        Assert.Equal(0.00m, aliceFinal.Net);
        Assert.Equal(0.00m, bobFinal.Net);
        Assert.Empty(settledBalances.SuggestedTransfers);

        // 7. Delete settlement as the creator (Bob)
        client.SetGroupToken(bobToken);
        var deleteSettlementRes = await client.DeleteAsync($"/api/groups/{group.Id}/settlements/{settlement.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteSettlementRes.StatusCode);
    }
}
