using System.Text.Json;
using Microsoft.Extensions.Options;
using Netstr.Json;
using Netstr.Messaging.Models;
using Netstr.Options;

namespace Netstr.Messaging.MessageHandlers
{
    public class LibregramMessageHandler : IMessageHandler
    {
        private const string CapabilitiesCommand = "lg.capabilities";

        private readonly IOptions<LibregramOptions> libregram;
        private readonly IOptions<RelayInformationOptions> relayInformation;

        public LibregramMessageHandler(
            IOptions<LibregramOptions> libregram,
            IOptions<RelayInformationOptions> relayInformation)
        {
            this.libregram = libregram;
            this.relayInformation = relayInformation;
        }

        public bool CanHandleMessage(string type) => type == MessageType.Libregram;

        public Task HandleMessageAsync(IWebSocketAdapter adapter, JsonDocument[] parameters)
        {
            if (parameters.Length < 3)
            {
                throw new UnknownMessageProcessingException("LG message should be an array with at least 3 elements");
            }

            var requestId = parameters[1].DeserializeRequired<string>();
            var command = parameters[2].DeserializeRequired<string>();

            if (!this.libregram.Value.Enabled)
            {
                adapter.SendLibregramError(requestId, "disabled: libregram extensions are disabled");
                return Task.CompletedTask;
            }

            if (command != CapabilitiesCommand)
            {
                adapter.SendLibregramError(requestId, $"unsupported: unknown libregram command {command}");
                return Task.CompletedTask;
            }

            adapter.SendLibregramOk(requestId, CreateCapabilitiesPayload());
            return Task.CompletedTask;
        }

        private Dictionary<string, object?> CreateCapabilitiesPayload()
        {
            var opts = this.libregram.Value;
            var relay = this.relayInformation.Value;

            return new()
            {
                ["is_libregram_relay"] = true,
                ["relay_flavor"] = opts.RelayFlavor,
                ["protocol"] = "libregram-relay",
                ["protocol_version"] = opts.ProtocolVersion,
                ["commands"] = opts.Commands,
                ["relay"] = new Dictionary<string, object?>
                {
                    ["name"] = relay.Name,
                    ["description"] = relay.Description,
                    ["public_key"] = relay.PublicKey,
                    ["contact"] = relay.Contact,
                    ["supported_nips"] = relay.SupportedNips ?? [],
                    ["version"] = relay.Version
                }
            };
        }
    }
}
