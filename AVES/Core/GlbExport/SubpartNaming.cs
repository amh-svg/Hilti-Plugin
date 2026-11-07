// -----------------------------------------------------------------------
// Title: Subpart Naming
// Summary: Builds the subpart name used in the BOM and on the GLB nodes.
// Key logic: This is the only place the name rule lives. The result must
//            match the schedule text exactly, so it is never trimmed or
//            cleaned.
// -----------------------------------------------------------------------

using Autodesk.Revit.DB;

namespace AVES.Core.GlbExport
{
    internal static class SubpartNaming
    {
        /// <summary>Builds the subpart name for a nested instance.</summary>
        /// <remarks>
        /// The name is the type's Element.Name. The "Family Name" and
        /// "Type Name" parameters are not used: they can carry a trailing
        /// space that the schedule text does not show.
        /// </remarks>
        public static string BuildName(FamilyInstance instance) => instance.Symbol.Name;
    }
}
