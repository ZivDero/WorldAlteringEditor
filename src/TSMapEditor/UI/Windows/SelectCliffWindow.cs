﻿using Rampastring.XNAUI;
using Rampastring.XNAUI.XNAControls;
using System;
using System.Linq;
using TSMapEditor.Models;
using TSMapEditor.UI.Controls;

namespace TSMapEditor.UI.Windows
{
    public class SelectCliffWindow : SelectObjectWindow<CliffType>
    {
        public SelectCliffWindow(WindowManager windowManager, Map map) : base(windowManager)
        {
            this.map = map;
        }

        private readonly Map map;
        public bool Success = false;

        public override void Initialize()
        {
            Name = nameof(SelectCliffWindow);
            base.Initialize();

            FindChild<EditorButton>("btnSelect").LeftClick += BtnSelect_LeftClick;
            lbObjectList.DoubleLeftClick += (s, e) => { Success = true; };
        }

        protected override void LbObjectList_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lbObjectList.SelectedItem == null)
            {
                SelectedObject = null;
                return;
            }

            SelectedObject = (CliffType)lbObjectList.SelectedItem.Tag;
        }

        protected void BtnSelect_LeftClick(object sender, EventArgs e)
        {
            Success = true;
        }

        public void Open()
        {
            Success = false;
            Open(null);
        }

        protected override void ListObjects()
        {
            lbObjectList.Clear();

            foreach (CliffType cliff in map.EditorConfig.Cliffs.Where(cliff =>
                         cliff.AllowedTheaters.Exists(theaterName => theaterName.Equals(map.TheaterName, StringComparison.OrdinalIgnoreCase))))
            {
                lbObjectList.AddItem(new XNAListBoxItem() { Text = cliff.Name, Tag = cliff });
            }
        }
    }
}