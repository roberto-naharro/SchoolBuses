using ColossalFramework;
using UnityEngine;

namespace SchoolBuses.Util
{
    // Snaps arbitrary world points onto the road network so generated stops land on
    // a drivable segment. Uses the NetManager segment grid (270×270 @ 64 m) — see
    // cs1/nets.md. Pure reads; safe on the simulation thread.
    internal static class RoadUtil
    {
        private const int GridResolution = 270;
        private const float CellSize = 64f;
        private const float HalfOffset = 135f; // GridResolution / 2

        // Returns the closest point on the nearest Road segment centreline within
        // maxDistance of `position`, or `position` unchanged if none found.
        // `found` reports whether a road was located.
        internal static Vector3 SnapToRoad(Vector3 position, float maxDistance, out bool found)
        {
            NetManager nm = Singleton<NetManager>.instance;
            var segments = nm.m_segments.m_buffer;
            var nodes = nm.m_nodes.m_buffer;

            int cellRange = Mathf.Max(1, Mathf.CeilToInt(maxDistance / CellSize));
            int cx = Mathf.Clamp((int)(position.x / CellSize + HalfOffset), 0, GridResolution - 1);
            int cz = Mathf.Clamp((int)(position.z / CellSize + HalfOffset), 0, GridResolution - 1);

            float bestSqr = maxDistance * maxDistance;
            Vector3 best = position;
            found = false;

            for (int dz = -cellRange; dz <= cellRange; dz++)
            {
                int gz = cz + dz;
                if (gz < 0 || gz >= GridResolution) continue;
                for (int dx = -cellRange; dx <= cellRange; dx++)
                {
                    int gx = cx + dx;
                    if (gx < 0 || gx >= GridResolution) continue;

                    ushort segId = nm.m_segmentGrid[gz * GridResolution + gx];
                    int guard = 0;
                    while (segId != 0)
                    {
                        NetInfo info = segments[segId].Info;
                        if (info != null && info.m_class != null
                            && info.m_class.m_service == ItemClass.Service.Road)
                        {
                            Vector3 a = nodes[segments[segId].m_startNode].m_position;
                            Vector3 b = nodes[segments[segId].m_endNode].m_position;
                            Vector3 p = ClosestPointOnSegment(position, a, b);
                            float sqr = (p - position).sqrMagnitude;
                            if (sqr < bestSqr)
                            {
                                bestSqr = sqr;
                                best = p;
                                found = true;
                            }
                        }
                        segId = segments[segId].m_nextGridSegment;
                        if (++guard > 32768) break;
                    }
                }
            }
            return best;
        }

        private static Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float lenSqr = ab.sqrMagnitude;
            if (lenSqr < 1e-4f)
                return a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / lenSqr);
            return a + ab * t;
        }

        internal static float Distance2D(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        internal static float SqrDistance2D(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }
    }
}
