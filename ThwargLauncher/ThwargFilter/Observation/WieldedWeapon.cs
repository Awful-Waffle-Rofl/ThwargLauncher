using System;

using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace ThwargFilter
{
    /// <summary>
    /// What the character is currently wielding, and which combat mode that implies.
    ///
    /// WHY THIS MATTERS: combat mode is NOT an independently settable axis. The backtick
    /// key toggles Peace and Combat, and WHICH combat mode you get is DERIVED from the
    /// wielded weapon: a wand gives Magic, a bow gives Missile, a melee weapon gives Melee.
    /// A player cannot reach "melee mode while carrying a wand" at all, and the client says
    /// so ("You can't enter melee mode while carrying a wand").
    ///
    /// That is very likely the real cause of ledger L6-76, where SetCombatMode appeared to
    /// no-op about half the time: those were mode/weapon mismatches, not a race. Asking for
    /// Melee while holding a wand is a request the client can only refuse, silently.
    /// </summary>
    class WieldedWeaponInfo
    {
        /// <summary>False when the world data could not be read at all.</summary>
        public bool Read;
        public int Id;
        public string Name;
        public string ObjectClass;
        /// <summary>True when ObjectClass maps to a known combat mode.</summary>
        public bool ImpliedKnown;
        /// <summary>The mode this weapon produces. Only meaningful when ImpliedKnown.</summary>
        public CombatState Implied = CombatState.Peace;

        public string Describe()
        {
            if (!Read) { return "weapon unknown (world data unreadable)"; }
            if (Name == null) { return "no weapon wielded"; }
            return string.Format(
                "'{0}' ({1}{2})",
                Name,
                ObjectClass,
                (ImpliedKnown ? " -> " + Implied.ToString() : " -> mode unknown"));
        }
    }

    class WieldedWeapon
    {
        // Verified ObjectClass members (Decal.Adapter 2.9.7.5):
        //   MeleeWeapon 1, MissileWeapon 9, WandStaffOrb 31
        // Note MissileWeapon covers bows AND arrows: Decal has no ObjectClass.Ammo.
        private const string CLASS_Melee = "MeleeWeapon";
        private const string CLASS_Missile = "MissileWeapon";
        private const string CLASS_Caster = "WandStaffOrb";

        /// <summary>
        /// Scan the wielded set for a weapon. MUST be called on the game thread.
        /// A character with no weapon is unarmed, which is Melee.
        /// </summary>
        public static WieldedWeaponInfo Read(int playerId)
        {
            WieldedWeaponInfo info = new WieldedWeaponInfo();
            try
            {
                WorldFilter worldFilter = CoreManager.Current.WorldFilter;
                if (worldFilter == null) { return info; }
                WorldObjectCollection owned = worldFilter.GetByOwner(playerId);
                if (owned == null) { return info; }

                info.Read = true;
                // Prefer a caster, then a missile weapon, then melee. A character wields one
                // weapon, but arrows share the MissileWeapon class with bows, and this order
                // keeps the answer stable if more than one weapon-class item is wielded.
                WorldObject best = null;
                int bestRank = int.MaxValue;
                foreach (WorldObject wo in owned)
                {
                    if (wo == null) { continue; }
                    // Equipped test: EquippedSlots != 0 ONLY. The Wielder arm was removed
                    // because the client ZEROES Wielder on unequip rather than removing it,
                    // so presence is true for just-unequipped items (ledger L8-5). Same
                    // discriminator as StateOracle and EquippedItems.
                    int equippedSlots = 0;
                    bool hasEquippedSlots = TryGetLong(wo, LongValueKey.EquippedSlots, out equippedSlots)
                        && equippedSlots != 0;
                    if (!hasEquippedSlots) { continue; }
                    string objectClass = SafeObjectClass(wo);
                    int rank = RankOf(objectClass);
                    if (rank < bestRank)
                    {
                        bestRank = rank;
                        best = wo;
                    }
                }

                if (best == null)
                {
                    // Nothing weapon-like wielded: unarmed, which the client treats as melee.
                    info.Name = null;
                    info.ObjectClass = null;
                    info.ImpliedKnown = true;
                    info.Implied = CombatState.Melee;
                    return info;
                }

                info.Id = SafeId(best);
                info.Name = SafeName(best);
                info.ObjectClass = SafeObjectClass(best);
                info.Implied = ImpliedMode(info.ObjectClass, out info.ImpliedKnown);
                return info;
            }
            catch (Exception exc)
            {
                log.WriteError("WieldedWeapon.Read exception: {0}", exc);
                return info;
            }
        }

        private static int RankOf(string objectClass)
        {
            if (objectClass == CLASS_Caster) { return 0; }
            if (objectClass == CLASS_Missile) { return 1; }
            if (objectClass == CLASS_Melee) { return 2; }
            return int.MaxValue;
        }

        /// <summary>The combat mode a weapon of this class produces.</summary>
        public static CombatState ImpliedMode(string objectClass, out bool known)
        {
            known = true;
            if (objectClass == CLASS_Caster) { return CombatState.Magic; }
            if (objectClass == CLASS_Missile) { return CombatState.Missile; }
            if (objectClass == CLASS_Melee) { return CombatState.Melee; }
            known = false;
            return CombatState.Peace;
        }

        /// <summary>
        /// Can the wielded weapon produce the requested mode? When it cannot, reason
        /// explains what to wield instead, so an impossible request fails fast with a
        /// diagnosable message rather than converging for 20 seconds against a client that
        /// will only ever refuse.
        /// </summary>
        public static bool CanProduce(CombatState desired, WieldedWeaponInfo info, out string reason)
        {
            reason = null;
            if (info == null || !info.Read)
            {
                // Unknown weapon: do not block. The toggle path does not need to know.
                return true;
            }
            if (!info.ImpliedKnown)
            {
                return true;
            }
            if (info.Implied == desired) { return true; }
            reason = string.Format(
                "requested {0} but wielded item is {1}, which produces {2}; wield a {3} first",
                desired,
                (info.Name == null ? "nothing" : "'" + info.Name + "' (" + info.ObjectClass + ")"),
                info.Implied,
                SuggestWeapon(desired));
            return false;
        }

        private static string SuggestWeapon(CombatState desired)
        {
            if (desired == CombatState.Magic) { return "wand, staff or orb"; }
            if (desired == CombatState.Missile) { return "bow, crossbow or thrown weapon"; }
            if (desired == CombatState.Melee) { return "melee weapon (or nothing, for unarmed)"; }
            return "different weapon";
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
