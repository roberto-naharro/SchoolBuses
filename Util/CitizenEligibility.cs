using ColossalFramework;
using SchoolBuses.Data;

namespace SchoolBuses.Util
{
    // The boarding gate (design §5). Called for every waiting citizen the boarding
    // loop considers on a school line, after the citizen's own TransportArriveAtSource
    // returned true. Must be allocation-free and side-effect-free.
    internal static class CitizenEligibility
    {
        // Admit citizen onto school line L iff:
        //   age ∈ {Child, Teen}
        //   ∧ Student
        //   ∧ m_workBuilding == L.school
        //   ∧ ( target == L.school                      (to-school, any stop)
        //       ∨ (target == home ∧ currentStop == L.schoolStop) )  (from-school, school stop only)
        internal static bool IsEligible(
            uint citizenId,
            ushort targetBuilding,
            ushort currentStop,
            ref SchoolLineData line)
        {
            if (citizenId == 0)
                return false;

            var citizens = Singleton<CitizenManager>.instance.m_citizens.m_buffer;

            // Age gate — excludes university (Young) and working adults.
            Citizen.AgeGroup age = Citizen.GetAgeGroup(citizens[citizenId].m_age);
            if (age != Citizen.AgeGroup.Child && age != Citizen.AgeGroup.Teen)
                return false;

            // Must be enrolled, and enrolled at *this* school.
            if ((citizens[citizenId].m_flags & Citizen.Flags.Student) == Citizen.Flags.None)
                return false;
            if (citizens[citizenId].m_workBuilding != line.SchoolBuildingId)
                return false;

            // A student of this school may ride to school OR home, boarding at any stop on
            // the line. (We originally limited the home leg to the school stop, but that
            // stopped kids using the bus to get home at all and got them wrongly evicted at
            // neighbourhood stops — so the gate is relaxed to "to school or home, any stop".)
            // currentStop is kept in the signature for callers but no longer constrains this.
            if (targetBuilding == line.SchoolBuildingId)
                return true;
            if (targetBuilding == citizens[citizenId].m_homeBuilding)
                return true;

            return false;
        }
    }
}
