using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace G3MagnetBoots
{
    internal struct HullTarget
    {
        public Part part;
        public Rigidbody rigidbody;
        public Collider collider;
        public Vector3 hitPoint;
        public Vector3 hitNormal;
        public float hitDistance;

        // log whjen target is valid
        public bool IsValid()
        {
            return this.part != null && this.collider != null && (G3MagnetBootsSettings.Current.enableOnAsteroids || !HullTargeting.IsVesselAsteroidOrComet(this.part.vessel));
        }
    }

    internal static class HullTargeting
    {
        private static readonly int HullMask = LayerUtil.DefaultEquivalent | 0x8000 | 0x80000;

        internal static bool TrySpherecast(Vector3 origin, Vector3 direction, float sphereRadius, float castLength,
            out RaycastHit hit, bool ignoreTriggers = false)
        {
            hit = default;
            return Physics.SphereCast(
                origin,
                sphereRadius,
                direction,
                out hit,
                castLength,
                HullMask,
                ignoreTriggers ? QueryTriggerInteraction.Ignore : QueryTriggerInteraction.Collide
            );
        }

        internal static bool TryRaycast(Vector3 origin, Vector3 direction, float castLength,
            out RaycastHit hit, bool ignoreTriggers = false)
        {
            hit = default;
            return Physics.Raycast(
                origin,
                direction,
                out hit,
                castLength,
                HullMask,
                ignoreTriggers ? QueryTriggerInteraction.Ignore : QueryTriggerInteraction.Collide
            );
        }

        internal static bool TryAcquireHullSpherecast(
            KerbalEVA kerbal,
            float upOffset,
            float sphereRadius,
            float castLength,
            float engageRadius,
            out HullTarget target)
        {
            target = default;

            if (kerbal == null || kerbal.footPivot == null) return false;

            // Spherecast from above the feet downwards
            Vector3 footPos = kerbal.footPivot.position;
            Vector3 up = kerbal.transform.up;
            Vector3 origin = footPos + up * upOffset;
            if (!TrySpherecast(origin, -up, sphereRadius, castLength, out var hit, ignoreTriggers: true))
                return false;

            // Ignore hits on kerbals (including self)
            Part hitPart = hit.collider.GetComponentInParent<Part>();
            if (hitPart == null || hitPart.GetComponent<KerbalEVA>() != null)
                return false;
        
            Vector3 hitPoint = hit.point;
            Vector3 closestPoint = hit.collider.ClosestPoint(footPos);

            Vector3 hitNormal = hit.normal;
            hitNormal.Normalize();

            // Spherecast might hit on its side, not necessarily the point right below the feet, so check actual distance
            float closestDistFromFoot = Vector3.Distance(footPos, hitPoint);
            if (closestDistFromFoot > engageRadius)
                return false;

            // Populate hull target
            target.part = hitPart;
            target.rigidbody = hitPart.rb;
            target.collider = hit.collider;
            target.hitPoint = hitPoint; // initial on radius
            target.hitNormal = hitNormal;
            target.hitDistance = closestDistFromFoot;

            return true;
        }

        internal static bool IsVesselAsteroidOrComet(Vessel v)
        {
            foreach (var mod in v.FindPartModulesImplementing<ModuleAsteroid>())
            {
                if (mod != null)
                    return true;
            }
            return false;
        }


        internal static float GetRelativeSpeedToHullPoint(this in HullTarget target, Part part)
        {
            if (part?.rb == null) return float.PositiveInfinity;
            Vector3 surfV = (target.rigidbody != null)
                ? target.rigidbody.GetPointVelocity(target.hitPoint)
                : Vector3.zero;
            return (part.rb.velocity - surfV).magnitude;
        }

        internal static Vector3 GetSurfacePointVelocity(this in HullTarget target)
        {
            if (target.rigidbody == null) return Vector3.zero;
            return target.rigidbody.GetPointVelocity(target.hitPoint);
        }


    }
}
