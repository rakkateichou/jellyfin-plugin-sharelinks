using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Plugin.ShareLinks.Models;
using Jellyfin.Plugin.ShareLinks.Services;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ShareLinks.Tests;

public sealed class GuestSessionCleanupTests
{
    [Fact]
    public async Task GuestSessionsEndBeforeDeletionAndOtherUsersStayConnected()
    {
        var user = new User("share-test", "auth", "reset") { Id = Guid.NewGuid() };
        var users = new Mock<IUserManager>();
        users.Setup(x => x.GetUserById(user.Id)).Returns(user);
        var devices = new Mock<IDeviceManager>();
        devices.Setup(x => x.GetDevices(It.Is<DeviceQuery>(q => q.UserId == user.Id)))
            .Returns(new QueryResult<Device> { Items = Array.Empty<Device>() });
        var sessions = new Mock<ISessionManager>();
        var active = new List<SessionInfo> {
            new(sessions.Object, NullLogger.Instance) { Id = "guest-one", UserId = user.Id },
            new(sessions.Object, NullLogger.Instance) { Id = "guest-two", UserId = user.Id },
            new(sessions.Object, NullLogger.Instance) { Id = "owner", UserId = Guid.NewGuid() }
        };
        sessions.SetupGet(x => x.Sessions).Returns(() => active);
        var deleted = false;
        sessions.Setup(x => x.ReportSessionEnded(It.IsAny<string>())).Returns((string id) => {
            Assert.False(deleted);
            Assert.NotEqual("owner", id);
            active.RemoveAll(x => x.Id == id);
            return ValueTask.CompletedTask;
        });
        users.Setup(x => x.DeleteUserAsync(user.Id)).Returns(() => {
            Assert.DoesNotContain(active, x => x.UserId == user.Id);
            deleted = true;
            return Task.CompletedTask;
        });
        var service = new JellyfinGuestUserService(users.Object, devices.Object,
            sessions.Object, NullLogger<JellyfinGuestUserService>.Instance);
        await service.DeleteGuestUserAsync(new ShareLinkRecord { GuestUserId = user.Id }, default);
        Assert.True(deleted);
        Assert.Equal("owner", Assert.Single(active).Id);
    }
}
