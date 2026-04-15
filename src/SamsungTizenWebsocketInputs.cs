using System;
using System.Collections.Generic;
using PepperDash.Essentials.Core.DeviceTypeInterfaces;

namespace PepperDash.Essentials.Plugin
{
    public class SamsungTizenWebsocketInputs : ISelectableItems<string>
    {
        private Dictionary<string, ISelectableItem> items = new Dictionary<string, ISelectableItem>();
        private string currentItem;

        public Dictionary<string, ISelectableItem> Items
        {
            get { return items; }
            set
            {
                if (items == value) return;
                items = value;
                ItemsUpdated?.Invoke(this, null);
            }
        }

        public string CurrentItem
        {
            get { return currentItem; }
            set
            {
                if (currentItem == value) return;
                currentItem = value;
                CurrentItemChanged?.Invoke(this, null);
            }
        }

        public event EventHandler ItemsUpdated;
        public event EventHandler CurrentItemChanged;
    }

    public class SamsungTizenWebsocketInput : ISelectableItem
    {
        private bool isSelected;
        private readonly SamsungTizenWebsocketController parent;

        public SamsungTizenWebsocketInput(string key, string name, SamsungTizenWebsocketController parent)
        {
            Key = key;
            Name = name;
            this.parent = parent;
        }

        public string Key { get; private set; }
        public string Name { get; private set; }

        public event EventHandler ItemUpdated;

        public bool IsSelected
        {
            get { return isSelected; }
            set
            {
                if (isSelected == value) return;
                isSelected = value;
                ItemUpdated?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Select()
        {
            parent.ExecuteSwitch(Key);
        }
    }
}
