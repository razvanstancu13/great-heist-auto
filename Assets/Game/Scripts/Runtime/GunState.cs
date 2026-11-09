using UnityEngine;

namespace GreatHeistAuto
{
    /// <summary>Magazine, cadence and reload of one gun in somebody's hands. Used by the player and the enemies.</summary>
    public class GunState
    {
        public readonly WeaponData Data;
        public int InMag;
        public float NextShotTime;
        public float ReloadEnd = -1f;
        /// <summary>Seconds between shots are multiplied by this.</summary>
        public float CadenceScale = 1f;

        public GunState(WeaponData data) { Data = data; InMag = data.MagazineSize; }

        public bool Reloading => ReloadEnd > 0f;
        public float ReloadProgress => Reloading ? 1f - Mathf.Clamp01((ReloadEnd - Time.time) / Data.ReloadSeconds) : 0f;

        public void Tick()
        {
            if (Reloading && Time.time >= ReloadEnd) { ReloadEnd = -1f; InMag = Data.MagazineSize; }
        }

        public bool StartReload()
        {
            if (Reloading || InMag >= Data.MagazineSize) return false;
            ReloadEnd = Time.time + Data.ReloadSeconds;
            return true;
        }

        public void CancelReload() => ReloadEnd = -1f;

        public bool CanFire => !Reloading && InMag > 0 && Time.time >= NextShotTime;

        public void ConsumeShot()
        {
            InMag--;
            NextShotTime = Time.time + Data.SecondsPerShot * CadenceScale;
        }

        public void Refill() { ReloadEnd = -1f; InMag = Data.MagazineSize; NextShotTime = 0f; }
    }
}
