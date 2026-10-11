# Booking form generator

`BookingFormItems.g.cs` is generated from Vector's RDLs (`TMS_SCT/Tms.Reports/TMS.Booking.{CY,CYD,CFS}.*.rdl`):

1. `python -I rdlform2cs.py <form>.rdl > forms/<form>.cs.txt` for each of the 13 forms. This prints every textbox, line and bordered rectangle of the RDL body at its own place, translates the expressions over `f`, and marks each tablix with `// TABLE`.
2. `python -I assemble_forms.py ../BookingFormItems.g.cs` joins them into one partial class, swapping the company letterhead for `f.CompanyName`.

`assemble_forms.py` reads the `forms/` folder next to itself. The tablixes are written by hand in `BookingForms.cs`.
