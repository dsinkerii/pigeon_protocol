using System.Text.Json;
using FluentAssertions;
using Netstr.Messaging.Models;
using TechTalk.SpecFlow;
using TechTalk.SpecFlow.Assist;

namespace Netstr.Tests.NIPs.Steps
{
    public partial class Steps
    {
        [When(@"(.*) sends a Libregram request (.*) (.*)")]
        public async Task WhenClientSendsALibregramRequest(string client, string requestId, string command)
        {
            var now = DateTimeOffset.UtcNow;
            var c = this.scenarioContext.Get<Clients>()[client];

            await c.WebSocket.SendAsync(
            [
                MessageType.Libregram,
                requestId,
                command,
                new { }
            ]);

            await c.WaitForMessageAsync(
                now,
                [MessageType.LibregramOk, requestId],
                [MessageType.LibregramError, requestId]);
        }

        [Then(@"(.*) receives a Libregram OK reply")]
        public void ThenClientReceivesALibregramOkReply(string client, Table table)
        {
            var expected = table.Rows.Single();
            var message = GetLastLibregramMessage(client, MessageType.LibregramOk, expected.GetString("Id"));
            var payload = (JsonElement)message[2];

            payload.GetProperty("is_libregram_relay").GetBoolean().Should().Be(expected.GetBoolean("IsLibregramRelay"));
            payload.GetProperty("commands")
                .EnumerateArray()
                .Select(x => x.GetString())
                .Should()
                .Contain(expected.GetString("Command"));
        }

        [Then(@"(.*) receives a Libregram error reply")]
        public void ThenClientReceivesALibregramErrorReply(string client, Table table)
        {
            var expected = table.Rows.Single();
            var message = GetLastLibregramMessage(client, MessageType.LibregramError, expected.GetString("Id"));

            message[2].Should().Be(expected.GetString("Message"));
        }

        private object[] GetLastLibregramMessage(string client, string type, string requestId)
        {
            return this.scenarioContext.Get<Clients>()[client]
                .GetReceivedMessages()
                .Last(x => x.Length >= 2 && x[0].Equals(type) && x[1].Equals(requestId));
        }
    }
}
