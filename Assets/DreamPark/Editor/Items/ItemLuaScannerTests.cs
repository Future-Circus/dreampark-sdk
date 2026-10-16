#if UNITY_EDITOR && !DREAMPARKCORE
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace DreamPark.Items
{
    public sealed class ItemLuaScannerTests
    {
        private const string Script = @"
-- @var potionId string ""potion_small""
-- @item cr_fox ""Fox""
-- @item cr_owl
local id = potionId
function onzoneenter()
    dp.profile.awardItem('coin', 5)
    dp.profile.awardItem(""gem"", 1, { cut = 'round' }, function(ok, it) end)
    dp.profile.awardUniqueItem('egg', { level = 1 })
    dp.profile.awardItem(id, 1)
    dp.profile.awardUniqueItem('cr_' .. species, { level = 1 })
    if dp.profile.hasItem('key_gold') then end
    dp.profile.removeItem(critter.instanceId, 1)
    dp.profile.getItems({ type = 'creature' })
    -- dp.profile.awardItem('ghost')
    --[[ dp.profile.awardItem('ghost2') ]]
end
";

        private static ItemLuaScanner.Discovery Find(ItemLuaScanner.Result r, string id)
        {
            return r.discoveries.FirstOrDefault(d => d.itemId == id);
        }

        [Test]
        public void FindsLiteralsVarsAndAnnotations()
        {
            var r = ItemLuaScanner.ScanText(Script, "Assets/Content/X/Scripts/t.lua.txt");
            var ids = r.discoveries.Select(d => d.itemId).ToList();
            Assert.That(ids, Is.SupersetOf(new[] { "coin", "gem", "egg", "potion_small", "cr_fox", "cr_owl", "key_gold" }));
            Assert.That(ids, Has.No.Member("ghost"));
            Assert.That(ids, Has.No.Member("ghost2"));
            Assert.That(Find(r, "cr_fox").source, Is.EqualTo(ItemStore.IdSource.LuaAnnotation));
            Assert.That(Find(r, "cr_fox").displayName, Is.EqualTo("Fox"));
            Assert.That(Find(r, "potion_small").source, Is.EqualTo(ItemStore.IdSource.LuaVarDefault));
        }

        [Test]
        public void MarksUniqueAwards()
        {
            var r = ItemLuaScanner.ScanText(Script);
            Assert.That(Find(r, "egg").unique, Is.True);
            Assert.That(Find(r, "gem").unique, Is.True, "awardItem with a metadata table is unique");
            Assert.That(Find(r, "coin").unique, Is.False);
        }

        [Test]
        public void UnreadableAwardIsReportedUnlessTheScriptDeclaresItems()
        {
            var declared = ItemLuaScanner.ScanText(Script);
            Assert.That(declared.unresolved, Is.Empty, "the script has @item lines");

            var bare = ItemLuaScanner.ScanText("dp.profile.awardUniqueItem('cr_' .. sp, {})\ndp.profile.removeItem(c.instanceId)");
            Assert.That(bare.unresolved.Select(u => u.expression), Is.EquivalentTo(new[] { "'cr_' .. sp" }),
                "awards are reported; reads/removes of instance ids are not");
        }

        [Test]
        public void MergeLocksDiscoveredKeepsManualCopyAndFillsAnnotationTitle()
        {
            var saved = new List<ItemStore.Entry>
            {
                new ItemStore.Entry { itemId = "coin", name = "Gold Coin", stackable = true },
                new ItemStore.Entry { itemId = "old", name = "Old", idSource = (int)ItemStore.IdSource.LuaLiteral },
                new ItemStore.Entry { itemId = "egg", stackable = false },
            };
            var merged = ItemStore.Merge(saved, ItemLuaScanner.ScanText(Script).discoveries);
            var coin = merged.First(e => e.itemId == "coin");
            Assert.That(coin.IdLocked, Is.True);
            Assert.That(coin.name, Is.EqualTo("Gold Coin"));
            Assert.That(merged.First(e => e.itemId == "old").IdLocked, Is.False, "no longer found → back to manual, not deleted");
            Assert.That(merged.First(e => e.itemId == "cr_fox").name, Is.EqualTo("Fox"));
            var egg = merged.First(e => e.itemId == "egg");
            Assert.That(egg.awardedUnique && !egg.stackable, Is.True);
            Assert.That(ItemsSection.StatusOf(egg).Key, Is.True, "unique + Stacks off warns");
        }

        [Test]
        public void SanitizeIdMatchesTheBackend()
        {
            Assert.That(ItemStore.SanitizeId(" cr fox!-1_a "), Is.EqualTo("crfox-1_a"));
            Assert.That(ItemStore.SanitizeId(new string('a', 100)).Length, Is.EqualTo(80));
        }
    }
}
#endif
