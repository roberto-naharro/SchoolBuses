using ICities;
using SchoolBuses.Data;
using SchoolBuses.UI;
using SchoolBuses.Util;
using UnityEngine;

namespace SchoolBuses
{
    // Auto-discovered by the game. Owns the persistent GameObject that hosts the two
    // UI panel-extenders, created on level load and destroyed on unload.
    public class LoadingExtension : LoadingExtensionBase
    {
        private GameObject _uiObject;

        public override void OnLevelLoaded(LoadMode mode)
        {
            base.OnLevelLoaded(mode);
            if (mode != LoadMode.NewGame && mode != LoadMode.LoadGame
                && mode != LoadMode.NewGameFromScenario)
                return;

            SchoolLineRegistry.PruneDeadLines();

            _uiObject = new GameObject("SchoolBusesUI");
            _uiObject.AddComponent<SchoolLinePanelExtender>();
            _uiObject.AddComponent<SchoolBuildingPanelExtender>();
            Log.Info("UI extenders mounted");
        }

        public override void OnLevelUnloading()
        {
            base.OnLevelUnloading();
            if (_uiObject != null)
            {
                Object.Destroy(_uiObject);
                _uiObject = null;
            }
            SchoolLineRegistry.Clear();
        }
    }
}
