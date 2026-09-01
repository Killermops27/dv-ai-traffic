using System;
using System.Collections.Generic;
using DV.ThingTypes;
using UnityEngine;

namespace AITraffic.Fleet
{
    /// <summary>
    /// Supported prototypical AI train consist configurations.
    /// </summary>
    public enum ConsistType
    {
        ShunterFreight,
        RegionalFreight,
        MainlineHeavy,
        PassengerCommuter
    }

    /// <summary>
    /// Provides prototypical AI consist definitions, livery resolution from game types,
    /// and consist generation for spawning ambient and job traffic.
    /// </summary>
    public static class ConsistDefinitions
    {
        private static readonly System.Random s_defaultRng = new System.Random();

        // Prototypical car pools for randomized consist generation
        private static readonly TrainCarType[] s_shunterFreightCars = new TrainCarType[]
        {
            TrainCarType.BoxcarBrown,
            TrainCarType.BoxcarGreen,
            TrainCarType.BoxcarRed,
            TrainCarType.FlatbedEmpty,
            TrainCarType.FlatbedStakes,
            TrainCarType.FlatbedShort,
            TrainCarType.GondolaRed,
            TrainCarType.StockBrown
        };

        private static readonly TrainCarType[] s_regionalFreightCars = new TrainCarType[]
        {
            TrainCarType.BoxcarBrown,
            TrainCarType.BoxcarGreen,
            TrainCarType.BoxcarPink,
            TrainCarType.RefrigeratorWhite,
            TrainCarType.FlatbedStakes,
            TrainCarType.FlatbedMilitary,
            TrainCarType.GondolaGreen,
            TrainCarType.GondolaGray,
            TrainCarType.TankBlue,
            TrainCarType.TankYellow,
            TrainCarType.TankShortMilk,
            TrainCarType.HopperBrown,
            TrainCarType.StockRed,
            TrainCarType.StockGreen
        };

        private static readonly TrainCarType[] s_mainlineHeavyCars = new TrainCarType[]
        {
            TrainCarType.HopperBrown,
            TrainCarType.HopperTeal,
            TrainCarType.HopperYellow,
            TrainCarType.HopperCoveredBrown,
            TrainCarType.TankOrange,
            TrainCarType.TankWhite,
            TrainCarType.TankBlack,
            TrainCarType.TankChrome,
            TrainCarType.AutorackRed,
            TrainCarType.AutorackBlue,
            TrainCarType.AutorackGreen,
            TrainCarType.AutorackYellow
        };

        private static readonly TrainCarType[] s_passengerCoaches = new TrainCarType[]
        {
            TrainCarType.PassengerRed,
            TrainCarType.PassengerGreen,
            TrainCarType.PassengerBlue
        };

        /// <summary>
        /// Retrieves the prototypical TrainCarLivery for a given TrainCarType from Derail Valley's object model.
        /// </summary>
        /// <param name="carType">The TrainCarType enum value.</param>
        /// <returns>The matching TrainCarLivery, or null if not found.</returns>
        public static TrainCarLivery GetLivery(TrainCarType carType)
        {
            try
            {
                if (DV.Globals.G.Types != null)
                {
                    if (DV.Globals.G.Types.TrainCarType_to_v2 != null)
                    {
                        TrainCarLivery livery;
                        if (DV.Globals.G.Types.TrainCarType_to_v2.TryGetValue(carType, out livery) && livery != null)
                        {
                            return livery;
                        }
                    }

                    if (DV.Globals.G.Types.Liveries != null)
                    {
                        for (int i = 0; i < DV.Globals.G.Types.Liveries.Count; i++)
                        {
                            var liv = DV.Globals.G.Types.Liveries[i];
                            if (liv != null && liv.v1 == carType)
                            {
                                return liv;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("Failed to lookup livery for car type '{0}': {1}", carType, ex.Message));
            }

            return null;
        }

        /// <summary>
        /// Generates a list of TrainCarLivery instances for the requested consist type using default RNG.
        /// </summary>
        /// <param name="type">The consist configuration type.</param>
        /// <returns>A list of liveries starting with the locomotive.</returns>
        public static List<TrainCarLivery> GetLiveries(ConsistType type)
        {
            return GetLiveries(type, s_defaultRng);
        }

        /// <summary>
        /// Generates a list of TrainCarLivery instances for the requested consist type with custom RNG.
        /// </summary>
        /// <param name="type">The consist configuration type.</param>
        /// <param name="rng">Random number generator for car count and variation.</param>
        /// <returns>A list of liveries starting with the locomotive.</returns>
        public static List<TrainCarLivery> GetLiveries(ConsistType type, System.Random rng)
        {
            if (rng == null) rng = s_defaultRng;
            List<TrainCarLivery> liveries = new List<TrainCarLivery>();

            switch (type)
            {
                case ConsistType.ShunterFreight:
                    BuildShunterFreight(liveries, rng);
                    break;

                case ConsistType.RegionalFreight:
                    BuildRegionalFreight(liveries, rng);
                    break;

                case ConsistType.MainlineHeavy:
                    BuildMainlineHeavy(liveries, rng);
                    break;

                case ConsistType.PassengerCommuter:
                    BuildPassengerCommuter(liveries, rng);
                    break;

                default:
                    BuildShunterFreight(liveries, rng);
                    break;
            }

            // Defensive check: Ensure at least one locomotive exists
            if (liveries.Count == 0)
            {
                TrainCarLivery fallbackLoco = GetLivery(TrainCarType.LocoShunter);
                if (fallbackLoco != null)
                    liveries.Add(fallbackLoco);
            }

            return liveries;
        }

        #region Consist Builders

        private static void BuildShunterFreight(List<TrainCarLivery> result, System.Random rng)
        {
            // Lead Locomotive: DE2 (LocoShunter)
            AddLivery(result, TrainCarType.LocoShunter);

            // 3 to 6 freight cars
            int carCount = rng.Next(3, 7);
            for (int i = 0; i < carCount; i++)
            {
                TrainCarType carType = s_shunterFreightCars[rng.Next(0, s_shunterFreightCars.Length)];
                AddLivery(result, carType);
            }

            // 50% chance of caboose on rear
            if (rng.NextDouble() > 0.5)
            {
                AddLivery(result, TrainCarType.CabooseRed);
            }
        }

        private static void BuildRegionalFreight(List<TrainCarLivery> result, System.Random rng)
        {
            // Lead Locomotive: DH4
            AddLivery(result, TrainCarType.LocoDH4);

            // 8 to 14 mixed freight cars
            int carCount = rng.Next(8, 15);
            for (int i = 0; i < carCount; i++)
            {
                TrainCarType carType = s_regionalFreightCars[rng.Next(0, s_regionalFreightCars.Length)];
                AddLivery(result, carType);
            }

            // Rear Caboose
            AddLivery(result, TrainCarType.CabooseRed);
        }

        private static void BuildMainlineHeavy(List<TrainCarLivery> result, System.Random rng)
        {
            // Lead Locomotive: DE6 (LocoDiesel)
            AddLivery(result, TrainCarType.LocoDiesel);

            // 30% chance of double-heading DE6 for heavy trains
            if (rng.NextDouble() < 0.3)
            {
                AddLivery(result, TrainCarType.LocoDiesel);
            }

            // 16 to 24 heavy hoppers / tanks / autoracks
            int carCount = rng.Next(16, 25);
            for (int i = 0; i < carCount; i++)
            {
                TrainCarType carType = s_mainlineHeavyCars[rng.Next(0, s_mainlineHeavyCars.Length)];
                AddLivery(result, carType);
            }

            // Rear Caboose
            AddLivery(result, TrainCarType.CabooseRed);
        }

        private static void BuildPassengerCommuter(List<TrainCarLivery> result, System.Random rng)
        {
            // Locomotive: 50% DM3 or 50% DE2
            TrainCarType loco = (rng.NextDouble() < 0.5) ? TrainCarType.LocoDM3 : TrainCarType.LocoShunter;
            AddLivery(result, loco);

            // 2 to 4 passenger coaches
            int coachCount = rng.Next(2, 5);
            for (int i = 0; i < coachCount; i++)
            {
                TrainCarType coach = s_passengerCoaches[rng.Next(0, s_passengerCoaches.Length)];
                AddLivery(result, coach);
            }
        }

        private static void AddLivery(List<TrainCarLivery> list, TrainCarType carType)
        {
            TrainCarLivery livery = GetLivery(carType);
            if (livery != null)
            {
                list.Add(livery);
            }
        }

        #endregion
    }
}
