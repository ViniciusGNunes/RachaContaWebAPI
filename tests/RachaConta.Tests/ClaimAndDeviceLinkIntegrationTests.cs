using System.Net;
using RachaConta.Api.Contracts;

namespace RachaConta.Tests;

public class ClaimAndDeviceLinkIntegrationTests : IClassFixture<RachaContaAppFactory>
{
    private readonly RachaContaAppFactory _factory;

    public ClaimAndDeviceLinkIntegrationTests(RachaContaAppFactory factory)
    {
        _factory = factory;
    }

    private async Task<GroupDto> CreateSampleGroupAsync(HttpClient client, string name = "Churrasco")
    {
        var req = new CreateGroupRequest(
            name,
            "Churrasco de domingo",
            [
                new MemberRequest("Lucas", null, null),
                new MemberRequest("Mariana", null, null),
            ]);

        var response = await client.PostAsJsonAsync("/api/groups", req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<GroupDto>();
        Assert.NotNull(dto);
        return dto;
    }

    [Fact]
    public async Task Claim_flow_and_conflicts()
    {
        var client = _factory.CreateClient();
        var group = await CreateSampleGroupAsync(client, "Grupo Claim");
        var lucas = group.Members.Single(m => m.Name == "Lucas");

        // 1. Claim existing unclaimed member using invite token
        client.SetGroupToken(group.AccessToken);
        var claimReq = new ClaimRequest(lucas.Id, null, "Lucas Celular");
        var claimRes = await client.PostAsJsonAsync($"/api/groups/{group.Id}/claim", claimReq);
        Assert.Equal(HttpStatusCode.OK, claimRes.StatusCode);

        var claimResult = await claimRes.Content.ReadFromJsonAsync<ClaimResultDto>();
        Assert.NotNull(claimResult);
        Assert.NotEmpty(claimResult.MemberToken);
        Assert.Equal(lucas.Id, claimResult.Member.Id);

        // 2. Claiming same member again -> 409 Conflict
        var secondClaim = await client.PostAsJsonAsync($"/api/groups/{group.Id}/claim", claimReq);
        Assert.Equal(HttpStatusCode.Conflict, secondClaim.StatusCode);

        // 3. Claiming as a new member
        var newMemberClaim = new ClaimRequest(null, "Novo Amigo", "Tablet");
        var newClaimRes = await client.PostAsJsonAsync($"/api/groups/{group.Id}/claim", newMemberClaim);
        Assert.Equal(HttpStatusCode.OK, newClaimRes.StatusCode);
        var newMemberResult = await newClaimRes.Content.ReadFromJsonAsync<ClaimResultDto>();
        Assert.NotNull(newMemberResult);
        Assert.Equal("Novo Amigo", newMemberResult.Member.Name);

        // 4. Calling claim endpoint with an identified member token -> 409 Conflict
        client.SetGroupToken(claimResult.MemberToken);
        var alreadyIdentifiedRes = await client.PostAsJsonAsync($"/api/groups/{group.Id}/claim", new ClaimRequest(null, "Outro", null));
        Assert.Equal(HttpStatusCode.Conflict, alreadyIdentifiedRes.StatusCode);
    }

    [Fact]
    public async Task Link_code_lifecycle_create_redeem_and_device_management()
    {
        var client = _factory.CreateClient();
        var group = await CreateSampleGroupAsync(client, "Grupo Link Code");
        var lucas = group.Members.Single(m => m.Name == "Lucas");

        // First claim Lucas
        client.SetGroupToken(group.AccessToken);
        var claimRes = await client.PostAsJsonAsync($"/api/groups/{group.Id}/claim", new ClaimRequest(lucas.Id, null, "Device 1"));
        var claimResult = await claimRes.Content.ReadFromJsonAsync<ClaimResultDto>();
        Assert.NotNull(claimResult);

        // Device 1 (identified as Lucas) creates link code
        client.SetGroupToken(claimResult.MemberToken);
        var codeRes = await client.PostAsync($"/api/groups/{group.Id}/members/me/link-codes", null);
        Assert.Equal(HttpStatusCode.OK, codeRes.StatusCode);

        var linkCode = await codeRes.Content.ReadFromJsonAsync<LinkCodeDto>();
        Assert.NotNull(linkCode);
        Assert.Equal(8, linkCode.Code.Length);
        Assert.True(linkCode.ExpiresAt > DateTime.UtcNow);

        // Device 2 redeems link code using group invite token
        client.SetGroupToken(group.AccessToken);
        var redeemRes = await client.PostAsJsonAsync($"/api/groups/{group.Id}/link-codes/redeem", new RedeemLinkCodeRequest(linkCode.Code, "Device 2"));
        Assert.Equal(HttpStatusCode.OK, redeemRes.StatusCode);

        var redeemed = await redeemRes.Content.ReadFromJsonAsync<ClaimResultDto>();
        Assert.NotNull(redeemed);
        Assert.Equal(lucas.Id, redeemed.Member.Id);
        Assert.NotEqual(claimResult.MemberToken, redeemed.MemberToken); // Distinct device token

        // Reusing the same code -> 404 NotFound
        var reuseRes = await client.PostAsJsonAsync($"/api/groups/{group.Id}/link-codes/redeem", new RedeemLinkCodeRequest(linkCode.Code, "Device 3"));
        Assert.Equal(HttpStatusCode.NotFound, reuseRes.StatusCode);

        // Wrong code -> 404 NotFound
        var wrongRes = await client.PostAsJsonAsync($"/api/groups/{group.Id}/link-codes/redeem", new RedeemLinkCodeRequest("WRONG123", "Device 3"));
        Assert.Equal(HttpStatusCode.NotFound, wrongRes.StatusCode);

        // List devices as Device 1
        client.SetGroupToken(claimResult.MemberToken);
        var devicesRes = await client.GetAsync($"/api/groups/{group.Id}/members/me/devices");
        Assert.Equal(HttpStatusCode.OK, devicesRes.StatusCode);
        var devices = await devicesRes.Content.ReadFromJsonAsync<List<DeviceDto>>();
        Assert.NotNull(devices);
        Assert.Equal(2, devices.Count);

        // Revoke Device 2 using Device 1
        var device2 = devices.Single(d => d.Label == "Device 2");
        var revokeRes = await client.DeleteAsync($"/api/groups/{group.Id}/members/me/devices/{device2.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revokeRes.StatusCode);

        // Trying to revoke the only remaining device -> 409 Conflict
        var remainingDevicesRes = await client.GetAsync($"/api/groups/{group.Id}/members/me/devices");
        var remainingDevices = await remainingDevicesRes.Content.ReadFromJsonAsync<List<DeviceDto>>();
        Assert.NotNull(remainingDevices);
        Assert.Single(remainingDevices);

        var revokeOnlyDevice = await client.DeleteAsync($"/api/groups/{group.Id}/members/me/devices/{remainingDevices[0].Id}");
        Assert.Equal(HttpStatusCode.Conflict, revokeOnlyDevice.StatusCode);
    }

    [Fact]
    public async Task Link_code_redeem_rate_limit_returns_429()
    {
        var client = _factory.CreateClient();
        var group = await CreateSampleGroupAsync(client, "Grupo Rate Limit");
        client.SetGroupToken(group.AccessToken);

        // FixedWindowRateLimiter has PermitLimit = 5 per minute.
        // First 5 requests should pass rate limiting and hit application logic (404 for wrong code).
        for (var i = 0; i < 5; i++)
        {
            var res = await client.PostAsJsonAsync($"/api/groups/{group.Id}/link-codes/redeem", new RedeemLinkCodeRequest($"CODE{i}ABC", "Label"));
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }

        // The 6th request should exceed the limit and return 429 Too Many Requests.
        var rateLimitedRes = await client.PostAsJsonAsync($"/api/groups/{group.Id}/link-codes/redeem", new RedeemLinkCodeRequest("CODE6ABC", "Label"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rateLimitedRes.StatusCode);
    }
}
