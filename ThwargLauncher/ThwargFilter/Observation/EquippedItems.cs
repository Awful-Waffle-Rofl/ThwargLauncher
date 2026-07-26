using System;
using System.Collections.Generic;

using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace ThwargFilter
{
    /// <summary>One item currently worn or wielded by the character.</summary>
    class EquippedItem
    {
        public int Id;
        public string Name;
        public string ObjectClass;
        /// <summary>LongValueKey.WieldingSlot, or -1 when the item does not carry it.</summary>
        public int WieldingSlot = -1;
        /// <summary>True when Wielder is this character (hand/wielded gear).</summary>
        public bool Wielded;
        /// <summary>True when the item carries a Coverage mask (worn armour or clothing).</summary>
        public bool Worn;

        public string Describe()
        {
            return string.Format(
                "'{0}' id={1} class={2} slot={3} {4}",
                Name,
                Id,
                ObjectClass,
                (WieldingSlot >= 0 ? WieldingSlot.ToString() : "?"),
                (Wielded ? "wielded" : "worn"));
        }
    }

    /// <summary>
    /// The equipped set, as a plain list rather than the oracle's JSON shape.
    ///
    /// WHY THIS MATTERS FOR UNWIELD: equipped items cannot be appraised. The filter's
    /// TargetResolver scans landscape objects and players only, so an equipped shield
    /// resolves as notfound, and every server-side dequip path that needs a last-appraised
    /// object is therefore unreachable for gear. This helper solves that: it already knows
    /// every wielded item, its id and its slot, so unwield resolves entirely client side.
    ///
    /// Same discriminators as the oracle: Wielder equals me for wielded gear, or a Coverage
    /// mask with no Container for worn armour. The container test matters, since a spare
    /// shirt in a pack also carries Coverage.
    ///
    /// MUST be called on the game thread.
    /// </summary>
    class EquippedItems
    {
        /// <summary>
        /// Returns null when the world data cannot be read at all, which is different from
        /// an empty list (readable world, nothing equipped).
        /// </summary>
        public static List<EquippedItem> Read(int playerId)
        {
            List<EquippedItem> found = new List<EquippedItem>();
            try
            {
                WorldFilter worldFilter = CoreManager.Current.WorldFilter;
                if (worldFilter == null) { return null; }
                WorldObjectCollection owned = worldFilter.GetByOwner(playerId);
                if (owned == null) { return null; }

                foreach (WorldObject wo in owned)
                {
                    if (wo == null) { continue; }

                    int container = 0;
                    bool hasContainer = TryGetLong(wo, LongValueKey.Container, out container) && container != 0;

                    int wielder = 0;
                    bool wielded = TryGetLong(wo, LongValueKey.Wielder, out wielder) && wielder == playerId;

                    int coverage = 0;
                    bool hasCoverage = TryGetLong(wo, LongValueKey.Coverage, out coverage);

                    if (!wielded && !(hasCoverage && !hasContainer)) { continue; }

                    EquippedItem item = new EquippedItem();
                    item.Id = SafeId(wo);
                    if (item.Id == 0) { continue; }
                    item.Name = SafeName(wo);
                    item.ObjectClass = SafeObjectClass(wo);
                    item.Wielded = wielded;
                    item.Worn = (!wielded && hasCoverage);
                    int slot = 0;
                    if (TryGetLong(wo, LongValueKey.WieldingSlot, out slot)) { item.WieldingSlot = slot; }
                    found.Add(item);
                }
                return found;
            }
            catch (Exception exc)
            {
                log.WriteError("EquippedItems.Read exception: {0}", exc);
                return null;
            }
        }

        /// <summary>
        /// Match by name substring, case insensitive. A target that parses as a plain
        /// integer is treated as a WIELDING SLOT instead, which is how a rig frees "whatever
        /// is in the offhand" without knowing its name.
        /// </summary>
        public static List<EquippedItem> Match(List<EquippedItem> items, string target, out bool matchedBySlot)
        {
            matchedBySlot = false;
            List<EquippedItem> hits = new List<EquippedItem>();
            if (items == null || target == null) { return hits; }
            string wanted = target.Trim();
            if (wanted.Length == 0) { return hits; }

            int slot = 0;
            if (int.TryParse(wanted, out slot))
            {
                matchedBySlot = true;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].WieldingSlot == slot) { hits.Add(items[i]); }
                }
                return hits;
            }

            for (int i = 0; i < items.Count; i++)
            {
                string name = items[i].Name;
                if (name == null) { continue; }
                if (name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                hits.Add(items[i]);
            }
            return hits;
        }

        public static bool ContainsId(List<EquippedItem> items, int id)
        {
            if (items == null) { return false; }
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Id == id) { return true; }
            }
            return false;
        }

        private static bool TryGetLong(WorldObject wo, LongValueKey key, out int value)
        {
            value = 0;
            try { return wo.Exists(key, out value); }
            catch (Exception) { return false; }
        }

        private static int SafeId(WorldObject wo)
        {
            try { return wo.Id; }
            catch (Exception) { return 0; }
        }

        private static string SafeName(WorldObject wo)
        {
            try { return wo.Name; }
            catch (Exception) { return null; }
        }

        private static string SafeObjectClass(WorldObject wo)
        {
            try { return wo.ObjectClass.ToString(); }
            catch (Exception) { return "Unknown"; }
        }
    }
}
