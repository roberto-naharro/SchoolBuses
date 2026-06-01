using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace SchoolBuses.Util
{
    // Helpers for identifying K–12 schools and reading their live student roster.
    // University (EducationLevel3) is deliberately excluded.
    internal static class EducationBuildingUtil
    {
        // Iteration guard — same magnitude the game uses on citizen-unit chains.
        private const int MaxUnitIterations = 524288;

        // True for an elementary (L1) or high school (L2) building, false for
        // university and every non-education building.
        internal static bool IsSchool(ushort buildingId)
        {
            if (buildingId == 0)
                return false;
            var buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            BuildingInfo info = buildings[buildingId].Info;
            if (info == null || info.m_buildingAI == null)
                return false;
            if (info.m_class == null || info.m_class.m_service != ItemClass.Service.Education)
                return false;
            BuildingAI ai = info.m_buildingAI;
            return ai.GetEducationLevel1() || ai.GetEducationLevel2();
        }

        internal static Vector3 GetPosition(ushort buildingId)
        {
            return Singleton<BuildingManager>.instance.m_buildings.m_buffer[buildingId].m_position;
        }

        // High school = EducationLevel2 (elementary = Level1). Lets the experiment pick a fixed
        // mix of small (elementary) and large (high) schools.
        internal static bool IsHighSchool(ushort buildingId)
        {
            if (!IsSchool(buildingId))
                return false;
            BuildingInfo info = Singleton<BuildingManager>.instance.m_buildings.m_buffer[buildingId].Info;
            return info != null && info.m_buildingAI != null && info.m_buildingAI.GetEducationLevel2();
        }

        // Every built K–12 school in the city (used by the experiment harness to generate routes
        // for all schools at once).
        internal static List<ushort> AllSchools()
        {
            var result = new List<ushort>();
            var buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            for (int i = 1; i < buildings.Length; i++)
            {
                if ((buildings[i].m_flags & Building.Flags.Created) == Building.Flags.None)
                    continue;
                if (IsSchool((ushort)i))
                    result.Add((ushort)i);
            }
            return result;
        }

        // Find a K–12 school within maxDistance of a world position (used to detect
        // which school an existing line's stop serves). Returns 0 if none.
        internal static ushort FindSchoolNear(Vector3 position, float maxDistance)
        {
            ushort id = Singleton<BuildingManager>.instance.FindBuilding(
                position, maxDistance,
                ItemClass.Service.Education, ItemClass.SubService.None,
                Building.Flags.Created, Building.Flags.Deleted);
            return IsSchool(id) ? id : (ushort)0;
        }

        // Walk the school's CitizenUnit chain and collect the home building id of
        // every enrolled student. Deterministic and building-precise (design §4.4).
        // Run on the simulation thread.
        internal static List<ushort> GetStudentHomeBuildings(ushort schoolId)
        {
            var homes = new List<ushort>();
            CollectStudents(schoolId, homes, null);
            return homes;
        }

        // Number of enrolled students currently registered at the school.
        internal static int GetEnrolledStudentCount(ushort schoolId)
        {
            return CollectStudents(schoolId, null, null);
        }

        // The school's student capacity (SchoolAI.m_studentCount), so the panel can show
        // "enrolled / capacity" — generating before the school has filled gives a thinner
        // route, so it helps to wait until enrolment is near capacity.
        internal static int GetStudentCapacity(ushort schoolId)
        {
            if (schoolId == 0)
                return 0;
            BuildingInfo info = Singleton<BuildingManager>.instance.m_buildings.m_buffer[schoolId].Info;
            SchoolAI ai = info != null ? info.m_buildingAI as SchoolAI : null;
            return ai != null ? ai.m_studentCount : 0;
        }

        // Core roster walk. Optionally appends each student's home building to
        // `homes` and/or each student id to `citizenIds`. Returns the student count.
        internal static int CollectStudents(ushort schoolId, List<ushort> homes, List<uint> citizenIds)
        {
            if (schoolId == 0)
                return 0;

            CitizenManager cm = Singleton<CitizenManager>.instance;
            var buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            var units = cm.m_units.m_buffer;
            var citizens = cm.m_citizens.m_buffer;

            int count = 0;
            uint unitId = buildings[schoolId].m_citizenUnits;
            int guard = 0;
            while (unitId != 0)
            {
                if ((units[unitId].m_flags & CitizenUnit.Flags.Student) != CitizenUnit.Flags.None)
                {
                    AddStudent(units[unitId].m_citizen0, citizens, buildings, homes, citizenIds, ref count);
                    AddStudent(units[unitId].m_citizen1, citizens, buildings, homes, citizenIds, ref count);
                    AddStudent(units[unitId].m_citizen2, citizens, buildings, homes, citizenIds, ref count);
                    AddStudent(units[unitId].m_citizen3, citizens, buildings, homes, citizenIds, ref count);
                    AddStudent(units[unitId].m_citizen4, citizens, buildings, homes, citizenIds, ref count);
                }
                unitId = units[unitId].m_nextUnit;
                if (++guard > MaxUnitIterations)
                {
                    Log.Warning("CollectStudents: citizen-unit chain overrun at school " + schoolId);
                    break;
                }
            }
            return count;
        }

        private static void AddStudent(
            uint citizenId, Citizen[] citizens, Building[] buildings,
            List<ushort> homes, List<uint> citizenIds, ref int count)
        {
            if (citizenId == 0)
                return;
            // Only count living K–12 students with a home to be picked up from.
            Citizen.AgeGroup age = Citizen.GetAgeGroup(citizens[citizenId].m_age);
            if (age != Citizen.AgeGroup.Child && age != Citizen.AgeGroup.Teen)
                return;
            ushort home = citizens[citizenId].m_homeBuilding;
            if (home == 0)
                return;
            // Skip students who live outside the city (their "home" is an outside
            // connection) — a local bus route can't pick them up there.
            if ((buildings[home].m_flags & Building.Flags.IncomingOutgoing) != Building.Flags.None)
                return;
            count++;
            if (homes != null)
                homes.Add(home);
            if (citizenIds != null)
                citizenIds.Add(citizenId);
        }
    }
}
