using Netstr.Messaging.Models;
using Netstr.Messaging.Negentropy;
using Netstr.Messaging.Subscriptions;

namespace Netstr.Messaging
{
    public interface IWebSocketListenerAdapter
    {
        Task StartAsync();
        ClientContext Context { get; }
    }

    public interface IWebSocketAdapter
    {
        void Send(MessageBatch batch);

        Task CloseAsync(string reason);

        ISubscriptionsAdapter Subscriptions { get; }
        
        INegentropyAdapter Negentropy { get; }

        ClientContext Context { get; }
    }

    public interface IWebSocketAdapterCollection
    {
        void Add(IWebSocketAdapter adapter);

        IWebSocketAdapter? GetById(string id);

        IEnumerable<IWebSocketAdapter> GetAll();

        void Remove(string id);
    }
}
