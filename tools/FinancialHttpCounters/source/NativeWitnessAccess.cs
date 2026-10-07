using System.Runtime.CompilerServices;

// The synthetic witness observes internal physical quiescence without widening
// the existing public collector API or inventing cleanup-success callbacks.
[assembly: InternalsVisibleTo("FinancialHttpCounters.NativeControls")]

namespace FinancialHttpCounters;

internal enum CollectorControlFault { TraceBudgetOneByte }
