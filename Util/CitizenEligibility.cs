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

            // Morning leg: heading to the school — board at any neighbourhood stop.
            if (targetBuilding == line.SchoolBuildingId)
                return true;

            // Afternoon leg: heading home — board only at the school stop. This also
            // cleanly rejects leisure→home trips at intermediate stops.
            if (targetBuilding == citizens[citizenId].m_homeBuilding
                && currentStop == line.SchoolStopNode)
                return true;

            return false;
        }
    }
}
