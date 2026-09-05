using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Services;
using Jellyfin.Plugin.ShareLinks.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ShareLinks.Tests;

public sealed class WaitingRoomTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sharelinks-waiting-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<ILibraryManager> _library = new();
    private readonly ShareLinkStore _store;
    private readonly ShareLinkCreationService _creation;
    private readonly Guid _roomId = Guid.NewGuid();
    private readonly Guid _creatorId = Guid.NewGuid();

    public WaitingRoomTests()
    {
        var paths = Mock.Of<IApplicationPaths>(value => value.DataPath == _directory);
        _store = new ShareLinkStore(paths, NullLogger<ShareLinkStore>.Instance);
        _creation = new ShareLinkCreationService(_store,
            new ShareTokenService(paths, NullLogger<ShareTokenService>.Instance),
            new ItemTagService(_library.Object, NullLogger<ItemTagService>.Instance),
            _library.Object, NullLogger<ShareLinkCreationService>.Instance);
    }

    private async Task<ShareLinkRecord> CreateWaitingInvite()
    {
        var (record, _) = await _creation.CreateAsync(null, _creatorId, 6, false, _roomId, null, default);
        record.ShareUrl = "https://jellyfin.example/j/test-invite";
        await _store.UpdateAsync(record);
        return record;
    }

    [Fact]
    public async Task EmptyRoomHasAnActiveInviteWithAnUnusedPermissionTag()
    {
        var record = await CreateWaitingInvite();
        Assert.Equal(ShareLinkStatus.Active, record.Status);
        Assert.Equal(string.Empty, record.ItemId);
        Assert.Null(record.WatchPartyMediaId);
        Assert.StartsWith("sharelinks-", record.AllowedTag);
        Assert.False(record.MetadataTouched);
        _library.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OrdinarySharesStillRequireAnItem()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _creation.CreateAsync(null, _creatorId, 6, false, null, null, default));
    }

    [Fact]
    public async Task SelectingATitlePreservesTheInviteAndExistingGuest()
    {
        var original = await CreateWaitingInvite();
        original.GuestUserId = Guid.NewGuid();
        original.Status = ShareLinkStatus.Redeemed;
        await _store.UpdateAsync(original);
        var movie = new Movie { Id = Guid.NewGuid(), Name = "First title" };

        var updated = await _creation.UpdatePartyAsync(_roomId, _creatorId, movie, movie.Id, default);

        Assert.NotNull(updated);
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(original.ShareUrl, updated.ShareUrl);
        Assert.Equal(original.TokenHash, updated.TokenHash);
        Assert.Equal(original.GuestUserId, updated.GuestUserId);
        Assert.Equal(original.AllowedTag, updated.AllowedTag);
        Assert.Contains(original.AllowedTag!, movie.Tags);
        Assert.Equal(movie.Id.ToString("D"), updated.ItemId);
        Assert.Equal(updated.ItemId, updated.WatchPartyMediaId);
        Assert.Equal(ShareLinkStatus.Redeemed, updated.Status);
    }

    [Fact]
    public async Task ANewTitleRemovesAccessToThePreviousTitle()
    {
        var original = await CreateWaitingInvite();
        var first = new Movie { Id = Guid.NewGuid(), Name = "First title" };
        var second = new Movie { Id = Guid.NewGuid(), Name = "Second title" };
        _library.Setup(value => value.GetItemById(first.Id)).Returns(first);
        await _creation.UpdatePartyAsync(_roomId, _creatorId, first, first.Id, default);

        var updated = await _creation.UpdatePartyAsync(_roomId, _creatorId, second, second.Id, default);

        Assert.DoesNotContain(original.AllowedTag!, first.Tags);
        Assert.Contains(original.AllowedTag!, second.Tags);
        Assert.Equal(original.ShareUrl, updated!.ShareUrl);
        Assert.Equal(second.Id.ToString("D"), updated.WatchPartyMediaId);
    }

    [Fact]
    public async Task AnEmptyCopyRequestCannotClearASelectedTitle()
    {
        await CreateWaitingInvite();
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Title" };
        await _creation.UpdatePartyAsync(_roomId, _creatorId, movie, movie.Id, default);
        var updated = await _creation.UpdatePartyAsync(_roomId, _creatorId, null, null, default);
        Assert.Equal(movie.Id.ToString("D"), updated!.WatchPartyMediaId);
    }

    [Fact]
    public async Task AnotherCreatorCannotChangeTheRoomsInvite()
    {
        await CreateWaitingInvite();
        var movie = new Movie { Id = Guid.NewGuid() };
        Assert.Null(await _creation.UpdatePartyAsync(_roomId, Guid.NewGuid(), movie, movie.Id, default));
        Assert.Empty(Assert.Single(await _store.ListAsync()).ItemId);
        _library.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ShareLinkStatus.Expired)]
    [InlineData(ShareLinkStatus.Revoked)]
    [InlineData(ShareLinkStatus.Failed)]
    public async Task FinishedInvitesAreNeverReactivated(ShareLinkStatus status)
    {
        var record = await CreateWaitingInvite();
        record.Status = status;
        await _store.UpdateAsync(record);
        Assert.Null(await _creation.UpdatePartyAsync(_roomId, _creatorId, null, null, default));
        Assert.Equal(status, Assert.Single(await _store.ListAsync()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedemptionRoutesEmptyRoomsWithoutAFakeMediaId(bool hasTitle)
    {
        var request = new DefaultHttpContext().Request;
        request.PathBase = "/jellyfin";
        Guid? itemId = hasTitle ? Guid.NewGuid() : null;
        var auth = new AuthenticationResult { User = new UserDto { Id = Guid.NewGuid() }, AccessToken = "test-token" };
        var build = typeof(ShareLinkRedemptionService).GetMethod("BuildBootstrapHtml", BindingFlags.NonPublic | BindingFlags.Static)!;
        var html = (string)build.Invoke(null, new object?[] { request, auth, itemId, _roomId })!;
        var redirect = JsonSerializer.Deserialize<string>(Regex.Match(html, @"const redirectUrl = (.+);").Groups[1].Value)!;
        Assert.Contains("jwpRoom=" + _roomId.ToString("D"), redirect);
        Assert.Contains("JwpRoomId: watchPartyRoomId", html);
        Assert.Contains("sessionStorage.removeItem('jwp_guest_closed')", html);
        if (hasTitle)
        {
            Assert.StartsWith("/jellyfin/web/index.html#/details?id=", redirect);
            Assert.Contains("jwpMedia=" + itemId!.Value.ToString("D"), redirect);
        }
        else
        {
            Assert.StartsWith("/jellyfin/web/index.html#/home?jwpWaiting=1", redirect);
            Assert.DoesNotContain("jwpMedia", redirect);
            Assert.DoesNotContain("details", redirect);
        }
    }

    public void Dispose()
    {
        // Each fixture owns only this generated directory under the test root.
        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sharelinks-waiting-tests")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(_directory);
        if (!target.StartsWith(testRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test cleanup path.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
