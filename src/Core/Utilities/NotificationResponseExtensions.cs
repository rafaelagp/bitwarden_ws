using System;
using Bit.Core.Models.Response;

namespace Bit.Core.Utilities
{
    public static class NotificationResponseExtensions
    {
        public static SyncCipherNotification ToSyncCipherNotification(this NotificationResponse notification)
        {
            var payload = (dynamic) notification.PayloadObject;
            var id = payload[nameof(SyncCipherNotification.Id)];
            var organizationId = payload[nameof(SyncCipherNotification.OrganizationId)];
            var userId = payload[nameof(SyncCipherNotification.UserId)];
            var revisionDate = (DateTime) payload[nameof(SyncCipherNotification.RevisionDate)];

            return new SyncCipherNotification
            {
                Id = id,
                OrganizationId = organizationId,
                UserId = userId,
                CollectionIds = null, //TODO add collectionIds
                RevisionDate = revisionDate
            };
        }

        public static SyncFolderNotification ToSyncFolderNotification(this NotificationResponse notification)
        {
            var payload = (dynamic)notification.PayloadObject;
            var id = payload[nameof(SyncCipherNotification.Id)];
            var userId = payload[nameof(SyncCipherNotification.UserId)];
            var revisionDate = (DateTime) payload[nameof(SyncCipherNotification.RevisionDate)];

            return new SyncFolderNotification
            {
                Id = id,
                UserId = userId,
                RevisionDate = revisionDate
            };
        }
    }
}
