using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Netstr.Messaging;
using Netstr.Messaging.MessageHandlers;
using Netstr.Options;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace Netstr.Tests
{
    public class LibregramMessageHandlerTests
    {
        [Fact]
        public async Task CapabilitiesReturnsLibregramAnchor()
        {
            var sent = new List<string>();
            var adapter = new Mock<IWebSocketAdapter>();
            adapter
                .Setup(x => x.Send(It.IsAny<MessageBatch>()))
                .Callback<MessageBatch>(batch => sent.AddRange(batch.Messages.Select(x => Encoding.UTF8.GetString(x))));

            var handler = new LibregramMessageHandler(
                OptionsFactory.Create(new LibregramOptions()),
                OptionsFactory.Create(new RelayInformationOptions
                {
                    Name = "pigeon protocol",
                    Description = "Libregram relay",
                    PublicKey = "abc",
                    Contact = "admin@example.com",
                    SupportedNips = [1, 42],
                    Version = "v1"
                }));

            using var message = JsonDocument.Parse("""["LG","req1","lg.capabilities",{}]""");

            await handler.HandleMessageAsync(adapter.Object, message.RootElement.EnumerateArray().Select(x => JsonDocument.Parse(x.GetRawText())).ToArray());

            sent.Should().ContainSingle();

            using var reply = JsonDocument.Parse(sent.Single());
            var root = reply.RootElement;

            root[0].GetString().Should().Be("LG-OK");
            root[1].GetString().Should().Be("req1");
            root[2].GetProperty("is_libregram_relay").GetBoolean().Should().BeTrue();
            root[2].GetProperty("protocol").GetString().Should().Be("libregram-relay");
            root[2].GetProperty("commands").EnumerateArray().Select(x => x.GetString()).Should().Contain("lg.capabilities");
            root[2].GetProperty("relay").GetProperty("name").GetString().Should().Be("pigeon protocol");
        }

        [Fact]
        public async Task UnknownCommandReturnsLibregramError()
        {
            var sent = new List<string>();
            var adapter = new Mock<IWebSocketAdapter>();
            adapter
                .Setup(x => x.Send(It.IsAny<MessageBatch>()))
                .Callback<MessageBatch>(batch => sent.AddRange(batch.Messages.Select(x => Encoding.UTF8.GetString(x))));

            var handler = new LibregramMessageHandler(
                OptionsFactory.Create(new LibregramOptions()),
                OptionsFactory.Create(new RelayInformationOptions()));

            using var message = JsonDocument.Parse("""["LG","req1","lg.nope",{}]""");

            await handler.HandleMessageAsync(adapter.Object, message.RootElement.EnumerateArray().Select(x => JsonDocument.Parse(x.GetRawText())).ToArray());

            using var reply = JsonDocument.Parse(sent.Single());

            reply.RootElement[0].GetString().Should().Be("LG-ERR");
            reply.RootElement[1].GetString().Should().Be("req1");
            reply.RootElement[2].GetString().Should().Be("unsupported: unknown libregram command lg.nope");
        }
    }
}
