using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace JSI
{
	public class RasterPropMonitorComputer: PartModule
	{
		public bool updateForced;
		// Data common for various variable calculations
		private int vesselNumParts;
		private int updateCountdown;
		private int dataUpdateCountdown;
		private int refreshTextRate = int.MaxValue;
		private int refreshDataRate = int.MaxValue;
		private Vector3d coM;
		private Vector3d up;
		private Vector3d forward;
		private Vector3d right;
		private Vector3d north;
		private Quaternion rotationVesselSurface;
		private Quaternion rotationSurface;
		private Vector3d velocityVesselSurface;
		private Vector3d velocityVesselOrbit;
		private Vector3d velocityRelativeTarget;
		private double speedVertical;
		private ITargetable target;
		private Vector3d targetSeparation;
		private Quaternion targetOrientation;
		private ManeuverNode node;
		private double time;
		private ProtoCrewMember[] vesselCrew;
		private double altitudeASL;
		private double altitudeTrue;
		private double altitudeBottom;
		private Orbit targetorbit;
		private bool orbitSensibility;
		private bool targetOrbitSensibility;
		private readonly DefaultableDictionary<string,Vector2d> resources = new DefaultableDictionary<string,Vector2d>(Vector2d.zero);
		private string[] resourcesAlphabetic;
		private double totalShipDryMass;
		private double totalShipWetMass;
		private double totalCurrentThrust;
		private double totalMaximumThrust;
		private double actualAverageIsp;
		private double totalDataAmount;
		private double secondsToImpact;
		private double localG;
		private double standardAtmosphere;
		// Local data fetching variables...
		private int gearGroupNumber;
		private int brakeGroupNumber;
		private int sasGroupNumber;
		private int lightGroupNumber;
		private int rcsGroupNumber;
		// Some constant things...
		private const double gee = 9.81d;
		private readonly Dictionary<string,string> namedResources = new Dictionary<string,string> {
			// Stock resources...
			{ "ELECTRIC", "ElectricCharge" },
			{ "FUEL", "LiquidFuel" },
			{ "OXIDIZER", "Oxidizer" },
			{ "MONOPROP", "MonoPropellant" },
			{ "RSINTAKEAIR", "IntakeAir" },
			{ "XENON", "XenonGas" },
			// Mod resources...
			{ "KETHANE", "Kethane" },
			// Modular fuels.
			{ "MFLH2", "LiquidH2" },
			{ "MFLOX", "LiquidOxygen" },
			{ "MFN2O4", "N2O4" },
			{ "MFMMH", "MMH" },
			{ "MFAEROZINE", "Aerozine" },
			{ "MFUDMH", "UDMH" },
			{ "MFHYDRAZINE", "Hydrazine" },
			{ "MFMETHANE", "Methane" },
			{ "MFNUCLEARFUEL", "nuclearFuel" },
			{ "MFNUCLEARWASTE", "nuclearWaste" },
			// Life support resources -- apparently common for TAC and Ioncross these days.
			{ "LSFOOD","Food" },
			{ "LSWATER","Water" },
			{ "LSOXYGEN","Oxygen" },
			{ "LSCO2","CarbonDioxide" },
			{ "LSWASTE","Waste" },
			{ "LSWASTEWATER","WasteWater" },
			// ECLSS resources
			{ "ECLSSCO2","CO2" },
			{ "ECLSSO2C","O2 Candle" },
			// Deadly reentry ablative shielding
			{ "ABLATIVESHIELD","AblativeShielding" },
		};
		// TODO: Figure out if I can keep it at Start or OnAwake is better since it's a PartModule now.
		public void Start()
		{
			// Well, it looks like we have to do that bit just like in Firespitter.
			gearGroupNumber = BaseAction.GetGroupIndex(KSPActionGroup.Gear);
			brakeGroupNumber = BaseAction.GetGroupIndex(KSPActionGroup.Brakes);
			sasGroupNumber = BaseAction.GetGroupIndex(KSPActionGroup.SAS);
			lightGroupNumber = BaseAction.GetGroupIndex(KSPActionGroup.Light);
			rcsGroupNumber = BaseAction.GetGroupIndex(KSPActionGroup.RCS);

			if (HighLogic.LoadedSceneIsFlight) {
				FetchPerPartData();
				standardAtmosphere = FlightGlobals.getAtmDensity(FlightGlobals.getStaticPressure(0, FlightGlobals.Bodies[1]));
			}
		}

		public void UpdateRefreshRates(int rate, int dataRate)
		{
			refreshTextRate = Math.Min(rate, refreshTextRate);
			refreshDataRate = Math.Min(dataRate, refreshDataRate);
		}

		private bool UpdateCheck()
		{
			if (vesselNumParts != vessel.Parts.Count || updateCountdown <= 0 || dataUpdateCountdown <= 0 || updateForced) {
				updateCountdown = refreshTextRate;
				if (vesselNumParts != vessel.Parts.Count || dataUpdateCountdown <= 0 || updateForced) {
					dataUpdateCountdown = refreshDataRate;
					vesselNumParts = vessel.Parts.Count;
					FetchPerPartData();
				}
				updateForced = false;
				return true;
			}
			dataUpdateCountdown--;
			updateCountdown--;
			return false;
		}

		public override void OnUpdate()
		{
			if (!HighLogic.LoadedSceneIsFlight || vessel != FlightGlobals.ActiveVessel)
				return;

			if (CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.IVA ||
			    CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.Internal) {

				if (!UpdateCheck())
					return;

				FetchCommonData();
			}
		}
		// Sigh. MechJeb math.
		private static double GetCurrentThrust(ModuleEngines engine)
		{
			if ((!engine.EngineIgnited) || (!engine.isEnabled) || (!engine.isOperational))
				return 0;
			return engine.finalThrust;
		}

		private static double GetMaximumThrust(ModuleEngines engine)
		{
			if ((!engine.EngineIgnited) || (!engine.isEnabled) || (!engine.isOperational))
				return 0;
			return engine.maxThrust;
		}

		private static double GetRealIsp(ModuleEngines engine)
		{
			if ((!engine.EngineIgnited) || (!engine.isEnabled) || (!engine.isOperational))
				return 0;
			return engine.realIsp;
		}

		public void FetchCommonData()
		{
			localG = vessel.orbit.referenceBody.GeeASL * gee;
			coM = vessel.findWorldCenterOfMass();
			up = (coM - vessel.mainBody.position).normalized;
			forward = vessel.GetTransform().up;
			right = vessel.GetTransform().right;
			north = Vector3d.Exclude(up, (vessel.mainBody.position + vessel.mainBody.transform.up * (float)vessel.mainBody.Radius) - coM).normalized;
			rotationSurface = Quaternion.LookRotation(north, up);
			rotationVesselSurface = Quaternion.Inverse(Quaternion.Euler(90, 0, 0) * Quaternion.Inverse(vessel.GetTransform().rotation) * rotationSurface);

			velocityVesselOrbit = vessel.orbit.GetVel();
			velocityVesselSurface = velocityVesselOrbit - vessel.mainBody.getRFrmVel(coM);

			speedVertical = Vector3d.Dot(velocityVesselSurface, up);
			target = FlightGlobals.fetch.VesselTarget;
			node = vessel.patchedConicSolver.maneuverNodes.Count > 0 ? vessel.patchedConicSolver.maneuverNodes[0] : null;
			time = Planetarium.GetUniversalTime();
			FetchAltitudes();
			if (target != null) {
				velocityRelativeTarget = vessel.orbit.GetVel() - target.GetOrbit().GetVel();
				targetSeparation = vessel.GetTransform().position - target.GetTransform().position;
				targetOrientation = target.GetTransform().rotation;
				targetorbit = target.GetOrbit();
				var targetVessel = target as Vessel;
				targetOrbitSensibility = targetVessel == null || OrbitMakesSense(targetVessel);
			} else {
				velocityRelativeTarget = targetSeparation = Vector3d.zero;
				targetOrientation = new Quaternion();
				targetOrbitSensibility = false;
			}
			orbitSensibility = OrbitMakesSense(vessel);
			if (vessel.situation == Vessel.Situations.SUB_ORBITAL || vessel.situation == Vessel.Situations.FLYING) {
				// Mental note: the local g taken from vessel.mainBody.GeeASL will suffice.
				//  t = (v+sqrt(v²+2gd))/g or something.
				secondsToImpact = (speedVertical + Math.Sqrt(Math.Pow(speedVertical, 2) + 2 * localG * altitudeTrue)) / localG;
			} else
				secondsToImpact = Double.NaN;

		}

		public void FetchPerPartData()
		{
			resources.Clear();
			totalShipDryMass = totalShipWetMass = totalCurrentThrust = totalMaximumThrust = 0;
			totalDataAmount = 0;
			double averageIspContribution = 0;

			foreach (Part thatPart in vessel.parts) {
				// The cute way of using vector2d in place of a tuple is from Firespitter.
				// Hey, it works.
				foreach (PartResource resource in thatPart.Resources) {
					resources[resource.resourceName] += new Vector2d(resource.amount, resource.maxAmount);
				}

				if (thatPart.physicalSignificance != Part.PhysicalSignificance.NONE) {

					totalShipDryMass += thatPart.mass;
					totalShipWetMass += thatPart.mass;
				}

				totalShipWetMass += thatPart.GetResourceMass();

				foreach (PartModule pm in thatPart.Modules) {
					if (!pm.isEnabled)
						continue;
					var thatEngineModule = pm as ModuleEngines;
					if (thatEngineModule != null) {
						totalCurrentThrust += GetCurrentThrust(thatEngineModule);
						totalMaximumThrust += GetMaximumThrust(thatEngineModule);
						double realIsp = GetRealIsp(thatEngineModule);
						if (realIsp > 0)
							averageIspContribution += GetMaximumThrust(thatEngineModule) / realIsp;
					} 
				}

				foreach (IScienceDataContainer container in thatPart.FindModulesImplementing<IScienceDataContainer>()) {
					foreach (ScienceData datapoint in container.GetData()) {
						if (datapoint != null)
							totalDataAmount += datapoint.dataAmount;
					}
				}

			}

			if (averageIspContribution > 0)
				actualAverageIsp = totalMaximumThrust / averageIspContribution;
			else
				actualAverageIsp = 0;

			resourcesAlphabetic = resources.Keys.ToArray();

			// Turns out, all those extra small tails in resources interfere with string formatting.
			foreach (string resource in resourcesAlphabetic) {
				Vector2d values = resources[resource];
				resources[resource] = new Vector2d(Math.Round(values.x, 2), Math.Round(values.y, 2));
			}

			Array.Sort(resourcesAlphabetic);
			// I seriously hope you don't have crew jumping in and out more than once per second.
			vesselCrew = (vessel.GetVesselCrew()).ToArray();
		}

		private double GetResourceByName(string resourceName)
		{
			return resources[resourceName].x;
		}

		private double GetMaxResourceByName(string resourceName)
		{
			return resources[resourceName].y;
		}
		// Another piece from MechJeb.
		private void FetchAltitudes()
		{
			altitudeASL = vessel.mainBody.GetAltitude(coM);
			RaycastHit sfc;
			if (Physics.Raycast(coM, -up, out sfc, (float)altitudeASL + 10000.0F, 1 << 15)) {
				altitudeTrue = sfc.distance;
			} else if (vessel.mainBody.pqsController != null) {
				// from here: http://kerbalspaceprogram.com/forum/index.php?topic=10324.msg161923#msg161923
				altitudeTrue = vessel.mainBody.GetAltitude(coM) -
				(vessel.mainBody.pqsController.GetSurfaceHeight(QuaternionD.AngleAxis(vessel.mainBody.GetLongitude(coM), Vector3d.down) *
				QuaternionD.AngleAxis(vessel.mainBody.GetLatitude(coM), Vector3d.forward) *
				Vector3d.right) - vessel.mainBody.pqsController.radius);
			} else
				altitudeTrue = vessel.mainBody.GetAltitude(coM);
			altitudeBottom = altitudeTrue;
			if (altitudeTrue < 500d) {
				foreach (Part p in vessel.parts) {
					if (p.collider != null) {
						Vector3d bottomPoint = p.collider.ClosestPointOnBounds(vessel.mainBody.position);
						double partBottomAlt = vessel.mainBody.GetAltitude(bottomPoint) - altitudeASL;
						altitudeBottom = Math.Max(0, Math.Min(altitudeBottom, partBottomAlt));
					}
				}
			}
		}

		private static bool OrbitMakesSense(Vessel thatvessel)
		{
			if (thatvessel.situation == Vessel.Situations.FLYING ||
			    thatvessel.situation == Vessel.Situations.SUB_ORBITAL ||
			    thatvessel.situation == Vessel.Situations.ORBITING ||
			    thatvessel.situation == Vessel.Situations.ESCAPING ||
			    thatvessel.situation == Vessel.Situations.DOCKED) // Not sure about this last one.
				return true;
			return false;
		}
		// According to C# specification, switch-case is compiled to a constant hash table.
		// So this is actually more efficient than a dictionary, who'd have thought.
		private static string SituationString(Vessel.Situations situation)
		{
			switch (situation) {
				case Vessel.Situations.FLYING:
					return "Flying";
				case Vessel.Situations.SUB_ORBITAL:
					return "Sub-orbital";
				case Vessel.Situations.ESCAPING:
					return "Escaping";
				case Vessel.Situations.LANDED:
					return "Landed";
				case Vessel.Situations.DOCKED:
					return "Docked"; // When does this ever happen exactly, I wonder?
				case Vessel.Situations.PRELAUNCH:
					return "Ready to launch";
				case Vessel.Situations.ORBITING:
					return "Orbiting";
				case Vessel.Situations.SPLASHED:
					return "Splashed down";
			}
			return "??!";
		}
		//TODO: I really should make that more sensible, I mean, FOUR boolean flags?...
		// These three are formatting functions. They're better off moved into the formatter class.
		private static string FormatDateTime(double seconds, bool signed, bool noyears, bool plusskip)
		{
			return FormatDateTime(seconds, signed, noyears, plusskip, false);
		}
		private static string FormatDateTime(double seconds, bool signed, bool noyears, bool plusskip, bool nodays)
		{
			// I'd love to know when exactly does this happen, but I'll let it slide for now..
			if (Double.IsNaN(seconds))
				return string.Empty;

			TimeSpan span = TimeSpan.FromSeconds(Math.Abs(seconds));
			int years = (int)Math.Floor(span.TotalDays / 365);
			span -= new TimeSpan(365 * years, 0, 0, 0);
			double fracseconds = Math.Round(span.TotalSeconds - Math.Floor(span.TotalSeconds), 1);

			string formatstring = (signed ? (plusskip ? "{0:+;-; }" : "{0: ;-; }") : string.Empty) + 
			                      (noyears ? string.Empty : "{1:00}:") + (nodays ? string.Empty : "{2:000}:") + "{3:00}:{4:00}:{5:00.0}";

			return String.Format(formatstring, Math.Sign(seconds), years, span.Days, span.Hours, span.Minutes, span.Seconds + fracseconds);

		}

		private static string AngleToDMS(double angle)
		{
			int degrees = (int)Math.Floor(Math.Abs(angle));
			int minutes = (int)Math.Floor(60 * (Math.Abs(angle) - degrees));
			int seconds = (int)Math.Floor(3600 * (Math.Abs(angle) - degrees - minutes / 60.0));

			return String.Format("{0:0}° {1:00}' {2:00}\"", degrees, minutes, seconds);
		}

		private static string LatitudeDMS(double latitude)
		{
			return AngleToDMS(latitude) + (latitude > 0 ? " N" : " S");
		}

		private static string LongitudeDMS(double longitude)
		{
			double clampedLongitude = JUtil.ClampDegrees180(longitude);
			return AngleToDMS(clampedLongitude) + (clampedLongitude > 0 ? " E" : " W");
		}

		public object ProcessVariable(string input)
		{

			// It's slightly more optimal if we take care of that before the main switch body.
			if (input.IndexOf("_", StringComparison.Ordinal) > -1) {
				string[] tokens = input.Split('_');

				// If input starts with "LISTR" we're handling it specially -- it's a list of all resources.
				// The variables are named like LISTR_<number>_<NAME|VAL|MAX>
				if (tokens.Length == 3 && tokens[0] == "LISTR") {
					ushort resourceID = Convert.ToUInt16(tokens[1]);
					switch (tokens[2]) {
						case "NAME":
							if (resourceID >= resources.Count)
								return string.Empty;
							return resourcesAlphabetic[resourceID];
						case "VAL":
							if (resourceID >= resources.Count)
								return 0;
							return resources[resourcesAlphabetic[resourceID]].x;
						case "MAX":
							if (resourceID >= resources.Count)
								return 0;
							return resources[resourcesAlphabetic[resourceID]].y;
					}


				}

				// We do similar things for crew rosters.
				// The syntax is therefore CREW_<index>_<FIRST|LAST|FULL>
				if (tokens.Length == 3 && tokens[0] == "CREW") { 
					ushort crewSeatID = Convert.ToUInt16(tokens[1]);
					if (tokens[2] == "PRESENT") {
						return crewSeatID >= vesselCrew.Length ? -1 : 1;
					}
					if (crewSeatID >= vesselCrew.Length)
						return string.Empty;
					string kerbalname = vesselCrew[crewSeatID].name;
					string[] tokenisedname = kerbalname.Split();
					switch (tokens[2]) {
						case "FIRST":
							return tokenisedname[0];
						case "LAST":
							return tokenisedname[1];
						case "FULL":
							return kerbalname;
						default:
							return "???!";
					}
				}
			}

			switch (input) {
			// Annoying as it is, those casts to double are not actually redundant:
			// Analysis disable RedundantCast

			// It's a bit crude, but it's simple enough to populate.
			// Would be a bit smoother if I had eval() :)

			// Speeds.
				case "VERTSPEED":
					return speedVertical;
				case "SURFSPEED":
					return velocityVesselSurface.magnitude;
				case "ORBTSPEED":
					return velocityVesselOrbit.magnitude;
				case "TRGTSPEED":
					return velocityRelativeTarget.magnitude;
				case "HORZVELOCITY":
					return (velocityVesselSurface - (speedVertical * up)).magnitude;
				case "EASPEED":
					return vessel.srf_velocity.magnitude * Math.Sqrt(vessel.atmDensity / standardAtmosphere);

			// The way Engineer does it...
				case "TGTRELX":
					return FlightGlobals.ship_tgtVelocity.x;
				case "TGTRELY":
					return FlightGlobals.ship_tgtVelocity.y;
				case "TGTRELZ":
					return FlightGlobals.ship_tgtVelocity.z;

			// Time to impact. This is quite imprecise, because a precise calculation pulls in pages upon pages of MechJeb code.
			// It accounts for gravity now, though. Pull requests welcome.
				case "TIMETOIMPACT":
					if (Double.IsNaN(secondsToImpact) || secondsToImpact > 365 * 24 * 60 * 60 || secondsToImpact < 0) {
						return string.Empty;
					}
					return FormatDateTime(secondsToImpact, false, true, false); 
				case "TIMETOIMPACTSECS":
					if (Double.IsNaN(secondsToImpact) || secondsToImpact > 365 * 24 * 60 * 60 || secondsToImpact < 0)
						return -1;
					return secondsToImpact;

			// Altitudes
				case "ALTITUDE":
					return altitudeASL;
				case "RADARALT":
					return altitudeTrue;
				case "ALTITUDEBOTTOM":
					return altitudeBottom;

			// Masses.
				case "MASSDRY":
					return totalShipDryMass;
				case "MASSWET":
					return totalShipWetMass;
				case "MASSRESOURCES":
					return totalShipWetMass - totalShipDryMass;

			// Thrust and related
				case "THRUST":
					return totalCurrentThrust;
				case "THRUSTMAX":
					return totalMaximumThrust;
				case "TWR":
					return totalCurrentThrust / (totalShipWetMass * localG);
				case "TWRMAX":
					return totalMaximumThrust / (totalShipWetMass * localG);
				case "ACCEL":
					return totalCurrentThrust / totalShipWetMass;
				case "MAXACCEL":
					return totalMaximumThrust / totalShipWetMass;
				case "GFORCE":
					return vessel.geeForce_immediate;
				case "REALISP":
					return actualAverageIsp;

			// Maneuvers
				case "MNODETIME":
					if (node != null)
						return FormatDateTime(-(node.UT - time), true, false, true);
					return string.Empty;
				case "MNODEDV":
					if (node != null)
						return node.GetBurnVector(vessel.orbit).magnitude;
					return 0;
				case "MNODEBURNTIME":
					if (node != null && totalMaximumThrust > 0 && actualAverageIsp > 0)
						//return FormatDateTime(node.GetBurnVector(vessel.orbit).magnitude / vessel.specificAcceleration,false,true,false);
						return FormatDateTime(
							actualAverageIsp * (1 - Math.Exp(-node.GetBurnVector(vessel.orbit).magnitude / actualAverageIsp / gee)) / (totalMaximumThrust / (totalShipWetMass * gee)),
							false, true, false, true);
					return string.Empty;
				case "MNODEEXISTS":
					return node == null ? -1 : 1;

			// Orbital parameters
				case "ORBITBODY":
					return vessel.orbit.referenceBody.name;
				case "PERIAPSIS":
					if (orbitSensibility)
						return FlightGlobals.ship_orbit.PeA;
					return 0;
				case "APOAPSIS":
					if (orbitSensibility)
						return FlightGlobals.ship_orbit.ApA;
					return 0;
				case "INCLINATION":
					if (orbitSensibility)
						return FlightGlobals.ship_orbit.inclination;
					return 0;
				case "ECCENTRICITY":
					if (orbitSensibility)
						return vessel.orbit.eccentricity;
					return 0;
			// Time to apoapsis and periapsis are converted to DateTime objects and their formatting trickery applies.
				case "ORBPERIOD":
					if (orbitSensibility)
						return FormatDateTime(vessel.orbit.period, false, false, false);
					return string.Empty;
				case "TIMETOAP":
					if (orbitSensibility)
						return FormatDateTime(vessel.orbit.timeToAp, false, false, false);
					return string.Empty;
				case "TIMETOPE":
					if (orbitSensibility)
						return vessel.orbit.eccentricity < 1 ? 
							FormatDateTime(vessel.orbit.timeToPe, true, false, false) : 
							FormatDateTime(-vessel.orbit.meanAnomaly / (2 * Math.PI / vessel.orbit.period), true, false, false);
					return string.Empty;
				case "ORBITMAKESSENSE":
					if (orbitSensibility)
						return (double)1;
						
					return (double)-1;

			// Time
				case "UT":
					return FormatDateTime(time + 365 * 24 * 60 * 60, false, false, false);
				case "MET":
					return FormatDateTime(vessel.missionTime, false, false, false);

			// Names!
				case "NAME":
					return vessel.vesselName;


			// Coordinates.
				case "LATITUDE":
					return vessel.mainBody.GetLatitude(coM);
				case "LONGITUDE":
					return JUtil.ClampDegrees180(vessel.mainBody.GetLongitude(coM));
				case "LATITUDETGT":
					if (target is Vessel)
						return target.GetVessel().mainBody.GetLatitude(target.GetTransform().position);
					return -1;
				case "LONGITUDETGT":
					if (target is Vessel)
						return JUtil.ClampDegrees180(target.GetVessel().mainBody.GetLatitude(target.GetTransform().position));
					return -1;

			// Coordinates in degrees-minutes-seconds. Strictly speaking it would be better to teach String.Format to handle them, but that is currently beyond me.
				case "LATITUDE_DMS":
					return LatitudeDMS(vessel.mainBody.GetLatitude(coM));
				case "LONGITUDE_DMS":
					return LongitudeDMS(vessel.mainBody.GetLongitude(coM));
				case "LATITUDETGT_DMS":
					if (target is Vessel)
						return LatitudeDMS(target.GetVessel().mainBody.GetLatitude(target.GetVessel().GetWorldPos3D()));
					return string.Empty;
				case "LONGITUDETGT_DMS":
					if (target is Vessel)
						return LongitudeDMS(JUtil.ClampDegrees180(target.GetVessel().mainBody.GetLongitude(target.GetVessel().GetWorldPos3D())));
					return string.Empty;


			// Orientation
				case "HEADING":
					return rotationVesselSurface.eulerAngles.y;
				case "PITCH":
					return (rotationVesselSurface.eulerAngles.x > 180) ? (360.0 - rotationVesselSurface.eulerAngles.x) : -rotationVesselSurface.eulerAngles.x;
				case "ROLL":
					return (rotationVesselSurface.eulerAngles.z > 180) ? (rotationVesselSurface.eulerAngles.z - 360.0) : rotationVesselSurface.eulerAngles.z;

			// Targeting. Probably the most finicky bit right now.
				case "TARGETNAME":
					if (target == null)
						return string.Empty;
					if (target is Vessel || target is CelestialBody)
						return target.GetName();
				// Later, I think I want to get this to return the ship's name, not the docking node name...
					if (target is ModuleDockingNode)
						return target.GetName();
					return "???!";
				case "TARGETDISTANCE":
					if (target != null)
						return Vector3.Distance(target.GetTransform().position, vessel.GetTransform().position);
					return -1;
				case "RELATIVEINCLINATION":
					if (target != null) {
						return targetorbit.referenceBody != vessel.orbit.referenceBody ?
							 -1 : 
							Math.Abs(Vector3d.Angle(vessel.GetOrbit().SwappedOrbitNormal(), targetorbit.SwappedOrbitNormal()));
					}
					return -1;
				case "TARGETORBITBODY":
					if (target != null)
						return targetorbit.referenceBody.name;
					return string.Empty;
				case "TARGETEXISTS":
					if (target == null)
						return -1;
					if (target is Vessel)
						return 1;
					return 0;
				case "TARGETSITUATION":
					if (target is Vessel)
						return SituationString(target.GetVessel().situation);
					return string.Empty;
				case "TARGETALTITUDE":
					if (target == null)
						return -1;
					var targetVessel = target as Vessel;
					if (targetVessel != null) {
						return targetVessel.mainBody.GetAltitude(targetVessel.findWorldCenterOfMass());
					}
					return targetorbit.altitude;
				case "TIMETOANWITHTARGET":
					if (target == null || !targetOrbitSensibility)
						return string.Empty;
					return FormatDateTime(vessel.GetOrbit().TimeOfAscendingNode(targetorbit, time) - time, true, false, false);
				case "TIMETODNWITHTARGET":
					if (target == null || !targetOrbitSensibility)
						return string.Empty;
					return FormatDateTime(vessel.GetOrbit().TimeOfDescendingNode(targetorbit, time) - time, true, false, false);

			// Ok, what are X, Y and Z here anyway?
				case "TARGETDISTANCEX":
					return Vector3d.Dot(targetSeparation, vessel.GetTransform().right);
				case "TARGETDISTANCEY":
					return Vector3d.Dot(targetSeparation, vessel.GetTransform().forward);
				case "TARGETDISTANCEZ":
					return Vector3d.Dot(targetSeparation, vessel.GetTransform().up);

			// I probably should return something else for vessels. But not sure what exactly right now.
				case "TARGETANGLEX":
					if (target != null) {
						var targetDockingNode = target as ModuleDockingNode;
						if (targetDockingNode != null)
							return JUtil.NormalAngle(-targetDockingNode.GetFwdVector(), forward, up);
						if (target is Vessel)
							return JUtil.NormalAngle(-target.GetFwdVector(), forward, up);
						return 0;
					}
					return 0;
				case "TARGETANGLEY":
					if (target != null) {
						var targetDockingNode = target as ModuleDockingNode;
						if (targetDockingNode != null)
							return JUtil.NormalAngle(-targetDockingNode.GetFwdVector(), forward, -right);
						if (target is Vessel) {
							JUtil.NormalAngle(-target.GetFwdVector(), forward, -right);
						}
						return 0;
					}
					return 0;
				case "TARGETANGLEZ":
					if (target != null) {
						var targetDockingNode = target as ModuleDockingNode;
						if (targetDockingNode != null)
							return JUtil.NormalAngle(targetDockingNode.GetTransform().up, up, -forward);
						if (target is Vessel) {
							return JUtil.NormalAngle(target.GetTransform().up, up, -forward);
						}
						return 0;
					}
					return 0;
			
			// There goes the neighbourhood...
				case "TARGETAPOAPSIS":
					if (target != null && targetOrbitSensibility)
						return targetorbit.ApA;
					return 0;
				case "TARGETPERIAPSIS":
					if (target != null && targetOrbitSensibility)
						return targetorbit.PeA;
					return 0;
				case "TARGETINCLINATION":
					if (target != null && targetOrbitSensibility)
						return targetorbit.inclination;
					return 0;
				case "TARGETECCENTRICITY":
					if (target != null && targetOrbitSensibility)
						return targetorbit.eccentricity;
					return 0;
				case "TARGETORBITALVEL":
					if (target != null && targetOrbitSensibility)
						return targetorbit.orbitalSpeed;
					return 0;
				case "TARGETTIMETOAP":
					if (target != null && targetOrbitSensibility)
						return FormatDateTime(targetorbit.timeToAp, false, false, false);
					return string.Empty;
				case "TARGETORBPERIOD":
					if (target != null && targetOrbitSensibility)
						return FormatDateTime(targetorbit.period, false, false, false);
					return string.Empty;
				case "TARGETTIMETOPE":
					if (target != null && targetOrbitSensibility)
						return vessel.orbit.eccentricity < 1 ? 
							FormatDateTime(targetorbit.timeToPe, true, false, false) : 
							FormatDateTime(-targetorbit.meanAnomaly / (2 * Math.PI / targetorbit.period), true, false, false);
					return string.Empty;

			// Analysis disable RedundantCast
			// FLight control status
				case "THROTTLE":
					return (double)vessel.ctrlState.mainThrottle;
				case "STICKPITCH":
					return (double)vessel.ctrlState.pitch;
				case "STICKROLL":
					return (double)vessel.ctrlState.roll;
				case "STICKYAW":
					return (double)vessel.ctrlState.yaw;
				case "STICKPITCHTRIM":
					return (double)vessel.ctrlState.pitchTrim;
				case "STICKROLLTRIM":
					return (double)vessel.ctrlState.rollTrim;
				case "STICKYAWTRIM":
					return (double)vessel.ctrlState.yawTrim;
				case "STICKRCSX":
					return (double)vessel.ctrlState.X;
				case "STICKRCSY":
					return (double)vessel.ctrlState.Y;
				case "STICKRCSZ":
					return (double)vessel.ctrlState.Z;				

			// Staging and other stuff
				case "STAGE":
					return Staging.CurrentStage;
				case "SITUATION":
					return SituationString(vessel.situation);
				case "RANDOM":
					return (double)UnityEngine.Random.value;
				case "PODTEMPERATURE":
					return (double)part.temperature;

			// SCIENCE!!
				case "SCIENCEDATA":
					return totalDataAmount;

			// Action group flags. To properly format those, use this format:
			// {0:on;0;OFF}
			// Casting it to double is redundant, but JSIVariableAnimator type conversions need it to work well.
				case "GEAR":
					return (double)FlightGlobals.ActiveVessel.ActionGroups.groups[gearGroupNumber].GetHashCode();
				case "BRAKES":
					return (double)FlightGlobals.ActiveVessel.ActionGroups.groups[brakeGroupNumber].GetHashCode();
				case "SAS":
					return (double)FlightGlobals.ActiveVessel.ActionGroups.groups[sasGroupNumber].GetHashCode();
				case "LIGHTS":
					return (double)FlightGlobals.ActiveVessel.ActionGroups.groups[lightGroupNumber].GetHashCode();
				case "RCS":
					return (double)FlightGlobals.ActiveVessel.ActionGroups.groups[rcsGroupNumber].GetHashCode();

			}


			// Named resources are all the same and better off processed like this:
			foreach (KeyValuePair<string, string> resourceType in namedResources) {
				if (input.StartsWith(resourceType.Key, StringComparison.Ordinal)) {
					return input.EndsWith("MAX", StringComparison.Ordinal) ? resources[resourceType.Value].y : resources[resourceType.Value].x;
				}
			}

			// Didn't recognise anything so we return the string we got, that helps debugging.
			return input;
		}
	}
}

