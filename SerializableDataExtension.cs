using ICities;
using SchoolBuses.Data;
using SchoolBuses.Util;

namespace SchoolBuses
{
    // Persists the per-save school-line registry into the save game. Auto-discovered.
    public class SerializableDataExtension : SerializableDataExtensionBase
    {
        private const string DataId = "SchoolBuses_Registry_v1";

        public override void OnLoadData()
        {
            base.OnLoadData();
            try
            {
                byte[] data = serializableDataManager.LoadData(DataId);
                SchoolLineRegistry.Deserialize(data);
            }
            catch (System.Exception ex)
            {
                Log.Error("OnLoadData failed: " + ex);
            }
        }

        public override void OnSaveData()
        {
            base.OnSaveData();
            try
            {
                serializableDataManager.SaveData(DataId, SchoolLineRegistry.Serialize());
            }
            catch (System.Exception ex)
            {
                Log.Error("OnSaveData failed: " + ex);
            }
        }
    }
}
