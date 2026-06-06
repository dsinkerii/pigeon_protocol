
using Microsoft.Extensions.Options;
using Netstr.Options;

namespace Netstr.RelayInformation
{
    public interface IRelayInformationService
    {
        RelayInformationModel GetDocument();
    }

    public class RelayInformationService : IRelayInformationService
    {
        private readonly IOptions<RelayInformationOptions> options;
        private readonly IOptions<LimitsOptions> limits;
        private readonly IOptions<BlossomOptions> blossom;

        public RelayInformationService(
            IOptions<RelayInformationOptions> options,
            IOptions<LimitsOptions> limits,
            IOptions<BlossomOptions> blossom)
        {
            this.options = options;
            this.limits = limits;
            this.blossom = blossom;
        }

        public RelayInformationModel GetDocument()
        {
            var opts = this.options.Value;
            var limits = this.limits.Value;
            var b = this.blossom.Value;

            return new RelayInformationModel
            {
                Name = opts.Name ?? RelayInformationDefaults.Name,
                Description = opts.Description ?? RelayInformationDefaults.Description,
                PublicKey = opts.PublicKey,
                Contact = opts.Contact,
                SupportedNips = opts.SupportedNips ?? [],
                Software = RelayInformationDefaults.Software,
                SoftwareVersion = opts.Version,
                Limits = new()
                {
                    MaxMessageLength = limits.MaxPayloadSize,
                    MinPowDifficulty = limits.Events.MinPowDifficulty,
                    CreatedAtLowerLimit = limits.Events.MaxCreatedAtLowerOffset,
                    CreatedAtUpperLimit = limits.Events.MaxCreatedAtUpperOffset,
                    MaxEventTags = limits.Events.MaxEventTags,
                    MaxLimit = limits.Subscriptions.MaxInitialLimit,
                    MaxFilters = limits.Subscriptions.MaxFilters,
                    MaxSubscriptionIdLength = limits.Subscriptions.MaxSubscriptionIdLength,
                    MaxSubscriptions = limits.Subscriptions.MaxSubscriptions
                },
                Blossom = b.Enabled ? new BlossomInfo
                {
                    Enabled = true,
                    MaxUploadSize = b.MaxUploadSizeBytes,
                    MaxPerUser = b.MaxStoragePerUserBytes,
                    MaxTotal = b.MaxTotalStorageBytes,
                    AllowedTypes = b.AllowedMimeTypes
                } : null
            };
        }
    }
}
