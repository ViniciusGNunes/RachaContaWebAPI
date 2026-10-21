using System.Net;
using RachaConta.Api.Contracts;
using RachaConta.Api.Domain;

namespace RachaConta.Tests;

public class SplitFlowIntegrationTests : IClassFixture<RachaContaAppFactory>
{
    private readonly RachaContaAppFactory _factory;

    public SplitFlowIntegrationTests(RachaContaAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Split_full_lifecycle_and_admin_flows()
    {
        var client = _factory.CreateClient();

        // 1. Create Split (Module 1)
        var createReq = new CreateSplitRequest(
            Title: "Pizza de Sexta",
            TotalAmount: 150.00m,
            PixKey: "fulano@email.com",
            PixKeyType: PixKeyType.Email,
            ReceiverName: "Fulano de Tal",
            ReceiverCity: "Sao Paulo",
            SplitMode: SplitMode.Equal,
            IncludeOwner: true,
            Participants:
            [
                new CreateSplitParticipantRequest("Fulano", null, true),
                new CreateSplitParticipantRequest("Ciclano", null, false),
                new CreateSplitParticipantRequest("Beltrano", null, false),
            ]);

        var createRes = await client.PostAsJsonAsync("/api/splits", createReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

        var splitAdmin = await createRes.Content.ReadFromJsonAsync<SplitAdminDto>();
        Assert.NotNull(splitAdmin);
        Assert.NotEmpty(splitAdmin.Slug);
        Assert.NotEmpty(splitAdmin.AdminToken!);
        Assert.Equal(3, splitAdmin.Participants.Count);

        var owner = splitAdmin.Participants.Single(p => p.IsOwner);
        Assert.True(owner.Paid);
        Assert.Equal(50.00m, owner.Amount);

        var ciclano = splitAdmin.Participants.Single(p => p.Name == "Ciclano");
        Assert.False(ciclano.Paid);
        Assert.False(ciclano.PaymentPending);

        // 2. Get Public Split
        var getPublicRes = await client.GetAsync($"/api/splits/{splitAdmin.Slug}");
        Assert.Equal(HttpStatusCode.OK, getPublicRes.StatusCode);
        var publicSplit = await getPublicRes.Content.ReadFromJsonAsync<SplitPublicDto>();
        Assert.NotNull(publicSplit);
        Assert.Equal(splitAdmin.Title, publicSplit.Title);
        Assert.Equal(1, publicSplit.PaidCount); // Owner is paid

        // 3. Get Pix for guest vs owner
        var guestPixRes = await client.GetAsync($"/api/splits/{splitAdmin.Slug}/participants/{ciclano.Id}/pix");
        Assert.Equal(HttpStatusCode.OK, guestPixRes.StatusCode);
        var guestPix = await guestPixRes.Content.ReadFromJsonAsync<SplitPixDto>();
        Assert.NotNull(guestPix);
        Assert.NotEmpty(guestPix.PixCode);
        Assert.Equal(50.00m, guestPix.Amount);

        var ownerPixRes = await client.GetAsync($"/api/splits/{splitAdmin.Slug}/participants/{owner.Id}/pix");
        Assert.Equal(HttpStatusCode.NotFound, ownerPixRes.StatusCode);

        // 4. Guest marks self paid -> paymentPending = true
        var markPaidRes = await client.PostAsync($"/api/splits/{splitAdmin.Slug}/participants/{ciclano.Id}/mark-paid", null);
        Assert.Equal(HttpStatusCode.OK, markPaidRes.StatusCode);
        var markedPublic = await markPaidRes.Content.ReadFromJsonAsync<SplitPublicDto>();
        Assert.NotNull(markedPublic);
        var markedCiclano = markedPublic.Participants.Single(p => p.Id == ciclano.Id);
        Assert.False(markedCiclano.Paid);
        Assert.True(markedCiclano.PaymentPending);
        Assert.Equal(1, markedPublic.PendingCount);

        // 5. Admin rejects claim
        var rejectRes = await client.PostAsync($"/api/splits/{splitAdmin.Slug}/admin/participants/{ciclano.Id}/reject-claim?token={splitAdmin.AdminToken}", null);
        Assert.Equal(HttpStatusCode.OK, rejectRes.StatusCode);
        var rejectedAdmin = await rejectRes.Content.ReadFromJsonAsync<SplitAdminDto>();
        Assert.NotNull(rejectedAdmin);
        var rejectedCiclano = rejectedAdmin.Participants.Single(p => p.Id == ciclano.Id);
        Assert.False(rejectedCiclano.Paid);
        Assert.False(rejectedCiclano.PaymentPending);

        // 6. Admin toggles paid
        var togglePaidRes = await client.PostAsync($"/api/splits/{splitAdmin.Slug}/admin/participants/{ciclano.Id}/toggle-paid?token={splitAdmin.AdminToken}", null);
        Assert.Equal(HttpStatusCode.OK, togglePaidRes.StatusCode);
        var toggledAdmin = await togglePaidRes.Content.ReadFromJsonAsync<SplitAdminDto>();
        Assert.NotNull(toggledAdmin);
        var confirmedCiclano = toggledAdmin.Participants.Single(p => p.Id == ciclano.Id);
        Assert.True(confirmedCiclano.Paid);
        Assert.False(confirmedCiclano.PaymentPending);
        Assert.Equal(2, toggledAdmin.PaidCount);

        // 7. Admin deletes split
        var deleteRes = await client.DeleteAsync($"/api/splits/{splitAdmin.Slug}?token={splitAdmin.AdminToken}");
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);

        var getAfterDelete = await client.GetAsync($"/api/splits/{splitAdmin.Slug}");
        Assert.Equal(HttpStatusCode.NotFound, getAfterDelete.StatusCode);
    }
}
