﻿using System;
using System.Collections.Generic;
using System.Text;
using gView.Framework.UI;
using gView.Framework.Carto;
using gView.Framework.UI.Events;
using gView.Framework.system;

namespace gView.Plugins.Network
{
    [RegisterPlugIn("216D616B-FA15-4052-BFDE-16B6346C4B7F")]
    public class RemoveNetworkElements : ITool
    {
        private IMapDocument _doc = null;
        private Module _module = null;

        #region ITool Member

        public string Name
        {
            get { return "Remove Network Elements"; }
        }

        public bool Enabled
        {
            get { return _module != null; }
        }

        public string ToolTip
        {
            get { return "Remove Network Elements"; }
        }

        public ToolType toolType
        {
            get { return ToolType.command; }
        }

        public object Image
        {
            get { return global::gView.Plugins.Network.Properties.Resources.remove; }
        }

        public void OnCreate(object hook)
        {
            if (hook is IMapDocument)
            {
                _doc = (IMapDocument)hook;
                _module = Module.GetModule(_doc);
            }
        }

        public void OnEvent(object MapEvent)
        {
            if (_module != null)
            {
                IDisplay display = (IDisplay)_doc.FocusMap;
                _module.RemoveAllNetworkGraphicElements(display);

                ((MapEvent)MapEvent).drawPhase = DrawPhase.Graphics;
                ((MapEvent)MapEvent).refreshMap = true;
            }
        }

        #endregion
    }
}
