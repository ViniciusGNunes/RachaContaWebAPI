using System.Net;
using RachaPix.Api.Contracts;

namespace RachaPix.Tests;

public class GroupFlowIntegrationTests : IClassFixture<RachaPixAppFactory>
{
    private readonly RachaPixAppFactory _factory;

    public GroupFlowIntegrationTests(RachaPixAppFactory factory)
    {
        _factory = factory;
    }

    private async Task<GroupDto> CreateSampleGroupAsync(HttpClient client, string name = "Viagem de Férias")
    {
        var req = new CreateGroupRequest(
            name,
            "Praia com os amigos",
            [
                new MemberRequest("Ana", null, null),
                new MemberRequest("Bruno", null, null),
            ]);

        var response = await client.PostAsJsonAsync("/api/groups", req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<GroupDto>();
        Assert.NotNull(dto);
        Assert.NotEqual(Guid.Empty, dto.Id);
        Assert.NotEmpty(dto.AccessToken!);
        Assert.NotEmpty(dto.AdminToken!);
        Assert.Equal(2, dto.Members.Count);
        return dto;
    }

    [Fact]
    public async Task Create_group_and_get_with_invite_token_and_admin_token()
    {
        var client = _factory.CreateClient();
        var group = await CreateSampleGroupAsync(client);

        // 1. Get with invite (access) token
        client.SetGroupToken(group.AccessToken);
        var getWithInvite = await client.GetAsync($"/api/groups/{group.Id}");
        Assert.Equal(HttpStatusCode.OK, getWithInvite.StatusCode);
        var groupFromInvite = await getWithInvite.Content.ReadFromJsonAsync<GroupDto>();
        Assert.NotNull(groupFromInvite);
        Assert.Equal(group.Name, groupFromInvite.Name);
        Assert.Null(groupFromInvite.AccessToken); // Token hidden when accessing with invite
        Assert.Null(groupFromInvite.AdminToken);
        Assert.False(groupFromInvite.IsAdmin);

        // 2. Get with admin token
        client.SetGroupToken(group.AdminToken);
        var getWithAdmin = await client.GetAsync($"/api/groups/{group.Id}");
        Assert.Equal(HttpStatusCode.OK, getWithAdmin.StatusCode);
        var groupFromAdmin = await getWithAdmin.Content.ReadFromJsonAsync<GroupDto>();
        Assert.NotNull(groupFromAdmin);
        Assert.True(groupFromAdmin.IsAdmin);
        Assert.Equal(group.AdminToken, groupFromAdmin.AdminToken);
    }

    [Fact]
    public async Task Token_auth_wrong_token_missing_token_and_unknown_group_return_404()
    {
        var client = _factory.CreateClient();
        var group = await CreateSampleGroupAsync(client);

        // 1. Missing token -> 404
        client.SetGroupToken(null);
        var resMissing = await client.GetAsync($"/api/groups/{group.Id}");
        Assert.Equal(HttpStatusCode.NotFound, resMissing.StatusCode);

        // 2. Wrong token -> 404
        client.SetGroupToken("invalid-wrong-token-12345");
        var resWrong = await client.GetAsync($"/api/groups/{group.Id}");
        Assert.Equal(HttpStatusCode.NotFound, resWrong.StatusCode);

        // 3. Unknown group id -> 404
        client.SetGroupToken(group.AccessToken);
        var resUnknown = await client.GetAsync($"/api/groups/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resUnknown.StatusCode);
    }

    [Fact]
    public async Task Admin_actions_require_admin_token()
    {
        var client = _factory.CreateClient();
        var group = await CreateSampleGroupAsync(client);

        // Invite token attempting update -> 403 Forbidden
        client.SetGroupToken(group.AccessToken);
        var forbiddenUpdate = await client.PutAsJsonAsync($"/api/groups/{group.Id}", new UpdateGroupRequest("Novo Nome", null));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenUpdate.StatusCode);

        // Admin token updating -> 200 OK
        client.SetGroupToken(group.AdminToken);
        var adminUpdate = await client.PutAsJsonAsync($"/api/groups/{group.Id}", new UpdateGroupRequest("Novo Nome Atualizado", "Descricao nova"));
        Assert.Equal(HttpStatusCode.OK, adminUpdate.StatusCode);
        var updated = await adminUpdate.Content.ReadFromJsonAsync<GroupDto>();
        Assert.NotNull(updated);
        Assert.Equal("Novo Nome Atualizado", updated.Name);

        // Invite token attempting delete -> 403 Forbidden
        client.SetGroupToken(group.AccessToken);
        var forbiddenDelete = await client.DeleteAsync($"/api/groups/{group.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenDelete.StatusCode);

        // Admin token deleting group -> 204 NoContent
        client.SetGroupToken(group.AdminToken);
        var adminDelete = await client.DeleteAsync($"/api/groups/{group.Id}");
        Assert.Equal(HttpStatusCode.NoContent, adminDelete.StatusCode);

        // Subsequent get -> 404 NotFound
        var getAfterDelete = await client.GetAsync($"/api/groups/{group.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getAfterDelete.StatusCode);
    }

    [Fact]
    public async Task Member_management_add_and_delete()
    {
        var client = _factory.CreateClient();
        var group = await CreateSampleGroupAsync(client);

        // Adding member with invite token -> 403 Forbidden (needs member identity or admin)
        client.SetGroupToken(group.AccessToken);
        var forbiddenAdd = await client.PostAsJsonAsync($"/api/groups/{group.Id}/members", new MemberRequest("Carlos", null, null));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenAdd.StatusCode);

        // Adding member with admin token -> 201 Created
        client.SetGroupToken(group.AdminToken);
        var adminAdd = await client.PostAsJsonAsync($"/api/groups/{group.Id}/members", new MemberRequest("Carlos", null, null));
        Assert.Equal(HttpStatusCode.Created, adminAdd.StatusCode);
        var addedMember = await adminAdd.Content.ReadFromJsonAsync<MemberDto>();
        Assert.NotNull(addedMember);
        Assert.Equal("Carlos", addedMember.Name);

        // Deleting member with admin token -> 204 NoContent
        var adminDeleteMember = await client.DeleteAsync($"/api/groups/{group.Id}/members/{addedMember.Id}");
        Assert.Equal(HttpStatusCode.NoContent, adminDeleteMember.StatusCode);
    }
}
