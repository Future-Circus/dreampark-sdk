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

        [Test]
        public void ManualEarnAtLinksShowOnTheirAttractionAndProp()
        {
            var boss = new BadgeStore.Entry
            {
                badgeId = "boss",
                earnAt = new List<string> { "A_Boss", "P_Shrine", "A_Missing" },
            };
            var paths = new Dictionary<string, string>
            {
                ["A_Boss"] = "Assets/A_Boss.prefab",
                ["P_Shrine"] = "Assets/P_Shrine.prefab",
            };

            var index = ContentUploaderPanel.BuildBadgePreviewIndex(
                new List<BadgeStore.Entry> { boss }, new BadgeAttributionScanner.Result(), paths);

            Assert.That(index["Assets/A_Boss.prefab"], Is.EquivalentTo(new[] { boss }));
            Assert.That(index["Assets/P_Shrine.prefab"], Is.EquivalentTo(new[] { boss }));
            Assert.That(index.Count, Is.EqualTo(2));
        }
    }
}
#endif
