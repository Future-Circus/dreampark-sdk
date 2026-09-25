#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using DreamPark.Badges;
using DreamPark.PreUploadChecks;
using NUnit.Framework;

namespace DreamPark
{
    public sealed class BadgePreviewIndexTests
    {
        [Test]
        public void ShowsBadgeOnlyOnRootsThatActuallyAwardIt()
        {
            var explorer = new BadgeStore.Entry
            {
                badgeId = "explorer",
                name = "Explorer",
                iconAssetPath = "Assets/Explorer.png",
            };
            var attribution = new BadgeAttributionScanner.Result();
            attribution.awardedByRoot["explorer"] = new List<ContentRootInfo>
            {
                new ContentRootInfo { assetPath = "Assets/Attraction.prefab", kind = ContentRootKindPublic.Attraction },
                new ContentRootInfo { assetPath = "Assets/Prop.prefab", kind = ContentRootKindPublic.Prop },
            };
            attribution.awardedByPlayer.Add("explorer");

            var index = ContentUploaderPanel.BuildBadgePreviewIndex(
                new List<BadgeStore.Entry> { explorer }, attribution);

            Assert.That(index["Assets/Attraction.prefab"], Is.EquivalentTo(new[] { explorer }));
            Assert.That(index["Assets/Prop.prefab"], Is.EquivalentTo(new[] { explorer }));
            Assert.That(index.ContainsKey("Assets/Other.prefab"), Is.False);
        }

        [Test]
        public void UnconfiguredBadgeStillHasPlaceholderForItsLuaId()
        {
            var attribution = new BadgeAttributionScanner.Result();
            attribution.awardedByRoot["secret"] = new List<ContentRootInfo>
            {
                new ContentRootInfo { assetPath = "Assets/Attraction.prefab", kind = ContentRootKindPublic.Attraction },
            };

            var index = ContentUploaderPanel.BuildBadgePreviewIndex(
                new List<BadgeStore.Entry>(), attribution);

            Assert.That(index["Assets/Attraction.prefab"][0].badgeId, Is.EqualTo("secret"));
            Assert.That(index["Assets/Attraction.prefab"][0].iconAssetPath, Is.Empty);
        }
    }
}
#endif
