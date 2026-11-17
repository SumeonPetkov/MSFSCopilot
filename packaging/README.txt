SkyLink - shared cockpit for Microsoft Flight Simulator 2024
=============================================================

Needs Windows 10/11 (64-bit) and MSFS 2024. Nothing else to install.


BEFORE THE FIRST FLIGHT
-----------------------
1. SimConnect.dll
   Copy SimConnect.dll into this folder, next to SkyLink.exe.
   It comes with the MSFS 2024 SDK:  <SDK folder>\SimConnect SDK\lib\SimConnect.dll
   (In MSFS: Options > General > Developers > Developer Mode, then Help > SDK Installer.)

2. Firewall
   The first time SkyLink starts, Windows asks whether to allow it. Allow it on BOTH
   private and public networks (VPN adapters often count as public).
   SkyLink uses UDP ports 47800, 47801 and 47802.


FLYING TOGETHER
---------------
1. Both pilots start SkyLink.exe (MSFS can be open or closed).
2. "Your address" (left) shows your IP. One pilot types the other's address under
   "Add your co-pilot" and presses +. Over the internet, use a VPN between the two PCs
   and its address. Within a few seconds you both appear under "Pilots".
3. One pilot presses "Create party", the other presses "Join".
4. Pick your seats (Captain = left seat, First Officer = right seat), an aircraft and livery
   you both have, and optionally import your SimBrief plan.
5. The Captain presses "Start flight". SkyLink starts MSFS if needed, loads the same flight on
   both PCs and puts each of you in your seat. The Captain flies first; use
   "Ask for controls" / "Take controls" / "Hand over" to swap.

If something doesn't work, open "Activity" at the bottom of the window: every step is logged
there.


COMMAND-LINE TOOL (optional)
----------------------------
cli\skylink.exe has the same engine without a window:
  cli\skylink.exe probe      what the loaded aircraft exposes, and what MSFS rejected
  cli\skylink.exe lint       check the aircraft definitions in the definitions folder


AIRCRAFT DEFINITIONS
--------------------
The "definitions" folder says what syncs for each aircraft. Aircraft without their own
definition use a generic one (lights, radios, autopilot) and SkyLink tells you so.
