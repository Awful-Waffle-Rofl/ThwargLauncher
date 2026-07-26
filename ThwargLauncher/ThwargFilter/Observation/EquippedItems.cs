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
        /// <summary>LongValueKey.Type, which is the weenie class id (wcid), or 0.</summary>
        public int Wcid;
        /// <summary>LongValueKey.StackCount, or 0 when the item does not stack.</summary>
        public int StackCount;
        /// <summary>The container this item sits in, or 0 when it is equipped.</summary>
        public int Container;
        /// <summary>
        /// LongValueKey.EquipableSlots: the slot mask the ITEM declares it can occupy.
        /// This is the right source for an explicit wield slot - the item says where it
        /// goes, so we never have to guess a mask.
        /// </summary>
        public int EquipableSlots;

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
                    item.Container = container;
                    int slot = 0;
                    if (TryGetLong(wo, LongValueKey.WieldingSlot, out slot)) { item.WieldingSlot = slot; }
                    int wcid = 0;
                    if (TryGetLong(wo, LongValueKey.Type, out wcid)) { item.Wcid = wcid; }
                    int stack = 0;
                    if (TryGetLong(wo, LongValueKey.StackCount, out stack)) { item.StackCount = stack; }
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
        /// Pack contents: everything carried that sits INSIDE a container, the complement
        /// of the equipped set. GetInventory() covers nested packs, so this is the full
        /// carried set. Returns null when the world data cannot be read at all.
        /// </summary>
        public static List<EquippedItem> ReadCarried(int playerId)
        {
            List<EquippedItem> found = new List<EquippedItem>();
            try
            {
                WorldFilter worldFilter = CoreManager.Current.WorldFilter;
                if (worldFilter == null) { return null; }
                WorldObjectCollection carried = worldFilter.GetInventory();
                if (carried == null) { return null; }

                foreach (WorldObject wo in carried)
                {
                    if (wo == null) { continue; }
                    int container = 0;
                    bool hasContainer = TryGetLong(wo, LongValueKey.Container, out container) && container != 0;
                    if (!hasContainer) { continue; }

                    EquippedItem item = new EquippedItem();
                    item.Id = SafeId(wo);
                    if (item.Id == 0) { continue; }
                    item.Name = SafeName(wo);
                    item.ObjectClass = SafeObjectClass(wo);
                    item.Container = container;
                    int wcid = 0;
                    if (TryGetLong(wo, LongValueKey.Type, out wcid)) { item.Wcid = wcid; }
                    int stack = 0;
                    if (TryGetLong(wo, LongValueKey.StackCount, out stack)) { item.StackCount = stack; }
                    int slots = 0;
                    if (TryGetLong(wo, LongValueKey.EquipableSlots, out slots)) { item.EquipableSlots = slots; }
                    found.Add(item);
                }
                return found;
            }
            catch (Exception exc)
            {
                log.WriteError("EquippedItems.ReadCarried exception: {0}", exc);
                return null;
            }
        }

        /// <summary>
        /// Find one equipped item by id, or null. Used to inspect an item AFTER a wield so
        /// the record can report what keys it actually ended up carrying.
        /// </summary>
        public static EquippedItem FindById(List<EquippedItem> items, int id)
        {
            if (items == null) { return null; }
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Id == id) { return items[i]; }
            }
            return null;
        }

        /// <summary>
        /// Exact-id addressing: "id:1234". This is the ONLY form that is unambiguous when a
        /// character carries several stacks of the same item, which is the normal case for
        /// ammunition. The oracle gives every stack's id, so a caller always has the means
        /// to be exact.
        /// Returns true when the target used the id: form, whether or not it matched.
        /// </summary>
        public static bool TryMatchById(List<EquippedItem> items, string target, out EquippedItem match)
        {
            match = null;
            if (target == null) { return false; }
            string wanted = target.Trim();
            if (wanted.Length <= 3) { return false; }
            if (string.Compare(wanted.Substring(0, 3), "id:", StringComparison.OrdinalIgnoreCase) != 0) { return false; }
            int id = 0;
            if (!int.TryParse(wanted.Substring(3).Trim(), out id)) { return true; }
            match = FindById(items, id);
            return true;
        }

        /// <summary>
        /// Sort a candidate list by stack count descending, so an ambiguous report lists the
        /// biggest stack first. This orders the REPORT only; it never picks a winner.
        /// </summary>
        public static void SortByStackDescending(List<EquippedItem> items)
        {
            if (items == null) { return; }
            items.Sort(new Comparison<EquippedItem>(CompareByStackDescending));
        }

        private static int CompareByStackDescending(EquippedItem left, EquippedItem right)
        {
            return right.StackCount.CompareTo(left.StackCount);
        }

        /// <summary>
        /// Candidate summaries for a chatlog record, so a caller can disambiguate by id
        /// without a second round trip to the oracle.
        /// </summary>
        public static List<object> DescribeCandidates(List<EquippedItem> items, int max)
        {
            List<object> list = new List<object>();
            if (items == null) { return list; }
            for (int i = 0; i < items.Count && i < max; i++)
            {
                Dictionary<string, object> c = new Dictionary<string, object>();
                c["id"] = items[i].Id;
                c["name"] = items[i].Name;
                c["stackCount"] = (items[i].StackCount != 0 ? (object)items[i].StackCount : null);
                c["wcid"] = (items[i].Wcid != 0 ? (object)items[i].Wcid : null);
                c["wieldingSlot"] = (items[i].WieldingSlot >= 0 ? (object)items[i].WieldingSlot : null);
                list.Add(c);
            }
            return list;
        }

        /// <summary>
        /// Match by name substring, case insensitive, OR by weenie class id when the target
        /// parses as an integer. Used by wield, where a bare number means a wcid.
        /// </summary>
        public static List<EquippedItem> MatchByNameOrWcid(List<EquippedItem> items, string target, out bool matchedByWcid)
        {
            matchedByWcid = false;
            List<EquippedItem> hits = new List<EquippedItem>();
            if (items == null || target == null) { return hits; }
            string wanted = target.Trim();
            if (wanted.Length == 0) { return hits; }

            int wcid = 0;
            if (int.TryParse(wanted, out wcid))
            {
                matchedByWcid = true;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Wcid == wcid) { hits.Add(items[i]); }
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
