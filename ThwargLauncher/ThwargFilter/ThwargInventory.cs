using System;
using System.Collections.Generic;
using System.Text;
using IdMap = System.Collections.Generic.Dictionary<int, string>;

using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace ThwargFilter
{
    class ThwargInventory
    {
        private IdMap _items = new IdMap();
        private bool disposed;
        // Auto identify on item selection is ON by default, preserving existing behavior.
        // A test rig turns it OFF so that its own appraisal target cannot drift: this hook
        // issues a real RequestId, which moves the SERVER's last-appraised object, and ACE
        // admin commands like /remove-vitae act on exactly that object.
        private bool _autoIdentifyEnabled = true;

        public ThwargInventory()
        {
            CoreManager.Current.ItemSelected += Current_ItemSelected;
        }

        public bool AutoIdentifyEnabled
        {
            get { return _autoIdentifyEnabled; }
        }

        /// <summary>
        /// Enable or disable the automatic RequestId on item selection.
        /// Returns true if the state actually changed.
        /// </summary>
        public bool SetAutoIdentifyEnabled(bool enabled)
        {
            bool changed = (_autoIdentifyEnabled != enabled);
            _autoIdentifyEnabled = enabled;
            if (changed)
            {
                log.WriteInfo(
                    "Inventory auto identify hook is now {0}; appraisal target drift from item selection is {1}",
                    (enabled ? "ON" : "OFF"),
                    (enabled ? "possible" : "suppressed"));
            }
            else
            {
                log.WriteInfo("Inventory auto identify hook already {0}", (enabled ? "ON" : "OFF"));
            }
            return changed;
        }


        public void Dispose()
        {
            Dispose(true);

            // Use SupressFinalize in case a subclass
            // of this type implements a finalizer.
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing)
        {
            // If you need thread safety, use a lock around these 
            // operations, as well as in your methods that use the resource.
            if (!disposed)
            {
                if (disposing)
                {
                    CoreManager.Current.ItemSelected -= Current_ItemSelected;
                }
                // Indicate that the instance has been disposed.
                disposed = true;
            }
        }

        private void Current_ItemSelected(object sender, ItemSelectedEventArgs e)
        {
            if (e.ItemGuid == 0) { return; }
            if (!_autoIdentifyEnabled)
            {
                // Suppressed by "/tf inventoryhook off" so a rig can pin the server's
                // appraisal target. Deliberately does not record the item as seen, so
                // normal behavior resumes cleanly when the hook is turned back on.
                log.WriteDebug("Item selected {0} - auto identify suppressed", e.ItemGuid);
                return;
            }
            if (!_items.ContainsKey(e.ItemGuid))
            {
                log.WriteDebug("Item selected {0} - sending request", e.ItemGuid);
                CoreManager.Current.Actions.RequestId(e.ItemGuid);
                _items[e.ItemGuid] = "Q";
            }
            else
            {
                log.WriteDebug("Item selected {0} - no request", e.ItemGuid);
            }
        }
        public void HandleInventoryCommand()
        {
            int count = 0;
            foreach (WorldObject wo in CoreManager.Current.WorldFilter.GetInventory())
            {
                ++count;
                if (!wo.HasIdData)
                {
                    log.WriteDebug("Lack id data for {0}", wo.Id);
                }
                else
                {
                    log.WriteDebug("Id {0}, ObjectClass {1} Name {2}", wo.Id, wo.Name, wo.ObjectClass);
                }
            }
            log.WriteDebug("Inventory listed {0} items", count);
        }
    }
}
