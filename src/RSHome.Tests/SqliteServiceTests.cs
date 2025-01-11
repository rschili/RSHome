
using Moq;
using RSHome.Services;

namespace RSHome.Tests;

public class ArchiveServiceTests
{
    [Test]
    public async Task RoundtripDiscordMessages()
    {
    var mockConfigService = new Mock<IConfigService>();
    mockConfigService.Setup(config => config.SqliteDbPath).Returns(":memory:");

    using var sqliteService = await SqliteService.CreateAsync(mockConfigService.Object);
    DateTimeOffset dto1 = DateTimeOffset.Now;
    DateTimeOffset dto2 = dto1.AddSeconds(1);
    await sqliteService.AddDiscordMessageAsync(1, dto1, 1, "MyUsername", "Test Message", true, 1);
    await sqliteService.AddDiscordMessageAsync(2, dto2, 2, "Other Username", "Test Response", false, 1);
    await sqliteService.AddDiscordMessageAsync(3, dto1, 4, "Test", "Test", false, 2); // another channel, should not be returned

    var messages = await sqliteService.GetLastDiscordMessagesForChannelAsync(1, 5);
    await Assert.That(messages.Count).IsEqualTo(2);
    var first = messages[0];
    await Assert.That(first.Id).IsEqualTo<ulong>(1);
    await Assert.That(first.Timestamp).IsEqualTo(dto1);
    await Assert.That(first.UserId).IsEqualTo<ulong>(1);
    await Assert.That(first.UserLabel).IsEqualTo("MyUsername");
    await Assert.That(first.Body).IsEqualTo("Test Message");
    await Assert.That(first.IsFromSelf).IsTrue();
    await Assert.That(first.ChannelId).IsEqualTo<ulong>(1);

    var second = messages[1];
    await Assert.That(second.Id).IsEqualTo<ulong>(2);
    await Assert.That(second.Timestamp).IsEqualTo(dto2);
    await Assert.That(second.UserId).IsEqualTo<ulong>(2);
    await Assert.That(second.UserLabel).IsEqualTo("Other Username");
    await Assert.That(second.Body).IsEqualTo("Test Response");
    await Assert.That(second.IsFromSelf).IsFalse();
    await Assert.That(second.ChannelId).IsEqualTo<ulong>(1);
    }
}