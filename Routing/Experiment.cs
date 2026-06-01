namespace SchoolBuses.Routing
{
    // Drives the search for the best DEFAULT generation settings. The user runs an IDENTICAL clean
    // save each time and the harness always picks the SAME fixed schools, so the clean way to
    // measure a parameter's effect is: apply ONE setting to ALL sampled schools per run, and vary it
    // ACROSS runs — then the SAME school across runs is a confound-free comparison. The agent edits
    // the values below each run, redeploys, the user runs + sends the log, results accrue in
    // .claude/plans/experiment_results.md. min/radius are settled (8 / 400); the live variable is
    // Pickup. RunId labels the run in the logs (SCHOOL health "built: comboN" shows it).
    internal static class Experiment
    {
        internal const int Elementary = 4;   // first N elementary schools
        internal const int HighSchools = 4;  // first M high schools

        // Pickup 2000 is the confirmed knee. Now confirming radius (thin: 1 clean point) and min
        // (never cleanly swept). R5 r300 / R6 r500 bracket radius vs R4's r400; R7 m6 / R8 m12 sweep
        // min — all vs R4 (r400 m8 p2000) on the SAME 8 schools.
        // Radius 400 CONFIRMED best (R5 r300 ≤ R4 r400 on every high school; r500 already beaten).
        // Now sweep min vs R4 (r400 m8 p2000): R6 m6 (more coverage, more routes), R7 m12 (fewer).
        // R6 m6 worse than R4 m8 (more buses + turnedAway, marginal ridership). R7 checks the high
        // end m12 (fewer routes, less coverage). If m12 < m8, lock min=8.
        internal const int RunId = 7;        // bump each run for log/results labelling
        internal const float Radius = 400f;
        internal const int Min = 12;         // R7: higher min
        internal const float Pickup = 2000f;
    }
}
