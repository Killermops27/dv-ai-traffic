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
    /// Pairs a physical rolling stock livery with its loaded cargo type.
    /// </summary>
    public struct ConsistCarSpec
    {
        public TrainCarLivery Livery;
        public CargoType Cargo;

        public ConsistCarSpec(TrainCarLivery livery, CargoType cargo = CargoType.None)
        {
            Livery = livery;
            Cargo = cargo;
        }

        public ConsistCarSpec(TrainCarType carType, CargoType cargo = CargoType.None)
        {
            Livery = ConsistDefinitions.GetLivery(carType);
            Cargo = cargo;
        }
    }

    /// <summary>
    /// Provides prototypical AI consist definitions, livery resolution from game types,
    /// industry-aware wagon selection, realistic cargo loading, and locomotive power/weight balancing.
    /// </summary>
    public static class ConsistDefinitions
    {
        private static readonly System.Random s_defaultRng = new System.Random();

        #region Industry Cargo & Wagon Pool Definitions

        private struct CarCargoOption
        {
            public TrainCarType CarType;
            public CargoType Cargo;

            public CarCargoOption(TrainCarType carType, CargoType cargo)
            {
                CarType = carType;
                Cargo = cargo;
            }
        }

        // Coal chains (Coal Mine <-> Steel Mill / Harbor)
        private static readonly TrainCarType[] s_coalHoppers = new TrainCarType[]
        {
            TrainCarType.HopperBrown,
            TrainCarType.HopperTeal,
            TrainCarType.HopperYellow
        };

        // Iron Ore chains (Iron Ore Mines <-> Steel Mill / Harbor)
        private static readonly TrainCarType[] s_ironOreHoppers = new TrainCarType[]
        {
            TrainCarType.HopperBrown,
            TrainCarType.HopperTeal,
            TrainCarType.HopperYellow,
            TrainCarType.HopperCoveredBrown
        };

        // Steel Mill outbound (Steel products to Machine Factory, Goods Factory, Harbor, City)
        private static readonly CarCargoOption[] s_steelMillOutbound = new CarCargoOption[]
        {
            new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.SteelRolls),
            new CarCargoOption(TrainCarType.FlatbedShort, CargoType.SteelBillets),
            new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.SteelSlabs),
            new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.SteelBentPlates),
            new CarCargoOption(TrainCarType.FlatbedShort, CargoType.SteelRails),
            new CarCargoOption(TrainCarType.GondolaGreen, CargoType.SteelBillets),
            new CarCargoOption(TrainCarType.GondolaGray, CargoType.SteelBentPlates),
            new CarCargoOption(TrainCarType.GondolaRed, CargoType.SteelRolls)
        };

        // Steel Mill inbound scrap & materials
        private static readonly CarCargoOption[] s_steelMillInboundScrap = new CarCargoOption[]
        {
            new CarCargoOption(TrainCarType.GondolaGreen, CargoType.ScrapMetal),
            new CarCargoOption(TrainCarType.GondolaGray, CargoType.ScrapMetal),
            new CarCargoOption(TrainCarType.GondolaRed, CargoType.ScrapMetal),
            new CarCargoOption(TrainCarType.TankBlack, CargoType.CrudeOil),
            new CarCargoOption(TrainCarType.TankOrange, CargoType.CrudeOil)
        };

        // Sawmill outbound (Lumber, plywood, sleepers, woodchips to factories & towns)
        private static readonly CarCargoOption[] s_sawmillOutbound = new CarCargoOption[]
        {
            new CarCargoOption(TrainCarType.FlatbedStakes, CargoType.Boards),
            new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.Plywood),
            new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.Sleepers),
            new CarCargoOption(TrainCarType.GondolaGreen, CargoType.WoodChips),
            new CarCargoOption(TrainCarType.GondolaRed, CargoType.ScrapWood),
            new CarCargoOption(TrainCarType.BoxcarBrown, CargoType.Boards),
            new CarCargoOption(TrainCarType.BoxcarGreen, CargoType.Plywood)
        };

        // Agriculture & Farm outbound (Farm/Forest Meadow -> Food Factory, Towns)
        private static readonly CarCargoOption[] s_farmOutbound = new CarCargoOption[]
        {
            new CarCargoOption(TrainCarType.HopperBrown, CargoType.Wheat),
            new CarCargoOption(TrainCarType.HopperTeal, CargoType.Corn),
            new CarCargoOption(TrainCarType.HopperCoveredBrown, CargoType.SunflowerSeeds),
            new CarCargoOption(TrainCarType.StockBrown, CargoType.Pigs),
            new CarCargoOption(TrainCarType.StockRed, CargoType.Cows),
            new CarCargoOption(TrainCarType.StockGreen, CargoType.Sheep),
            new CarCargoOption(TrainCarType.TankShortMilk, CargoType.Milk),
            new CarCargoOption(TrainCarType.RefrigeratorWhite, CargoType.Eggs)
        };

        // Food Factory outbound (Food, beverages, canned goods to Goods Factory, Harbor, Cities)
        private static readonly CarCargoOption[] s_foodFactoryOutbound = new CarCargoOption[]
        {
            new CarCargoOption(TrainCarType.RefrigeratorWhite, CargoType.MeatProducts),
            new CarCargoOption(TrainCarType.RefrigeratorWhite, CargoType.DairyProducts),
            new CarCargoOption(TrainCarType.RefrigeratorWhite, CargoType.TemperateFruits),
            new CarCargoOption(TrainCarType.RefrigeratorWhite, CargoType.Vegetables),
            new CarCargoOption(TrainCarType.BoxcarBrown, CargoType.Bread),
            new CarCargoOption(TrainCarType.BoxcarGreen, CargoType.CannedFood),
            new CarCargoOption(TrainCarType.BoxcarRed, CargoType.CatFood),
            new CarCargoOption(TrainCarType.BoxcarPink, CargoType.Flour),
            new CarCargoOption(TrainCarType.TankBlue, CargoType.Alcohol)
        };

        // Oil Wells & Refinery (Crude oil, fuel, gas, chemicals)
        private static readonly CarCargoOption[] s_oilRefineryOutbound = new CarCargoOption[]
        {
            new CarCargoOption(TrainCarType.TankYellow, CargoType.Diesel),
            new CarCargoOption(TrainCarType.TankOrange, CargoType.Gasoline),
            new CarCargoOption(TrainCarType.TankWhite, CargoType.Methane),
            new CarCargoOption(TrainCarType.TankBlue, CargoType.ChemicalsSperex),
            new CarCargoOption(TrainCarType.TankChrome, CargoType.ChemicalsIskar),
            new CarCargoOption(TrainCarType.TankBlack, CargoType.CrudeOil),
            new CarCargoOption(TrainCarType.TankWhite, CargoType.Argon),
            new CarCargoOption(TrainCarType.TankBlue, CargoType.Nitrogen)
        };

        // Machine Factory outbound (Heavy equipment, machinery, tractors, tools)
        private static readonly CarCargoOption[] s_machineFactoryOutbound = new CarCargoOption[]
        {
            new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.Tractors),
            new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.Excavators),
            new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.MiningTrucks),
            new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.CityBuses),
            new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.SemiTrailers),
            new CarCargoOption(TrainCarType.FlatbedShort, CargoType.CraneParts),
            new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.Trams),
            new CarCargoOption(TrainCarType.BoxcarBrown, CargoType.ToolsIskar),
            new CarCargoOption(TrainCarType.BoxcarGreen, CargoType.ToolsBrohm),
            new CarCargoOption(TrainCarType.BoxcarRed, CargoType.TrainPartsDH4),
            new CarCargoOption(TrainCarType.BoxcarBrown, CargoType.TrainPartsDE6)
        };

        // Goods Factory & Harbor outbound (Consumer goods, electronics, autoracks, intermodal containers)
        private static readonly CarCargoOption[] s_goodsAndHarborOutbound = new CarCargoOption[]
        {
            new CarCargoOption(TrainCarType.AutorackRed, CargoType.ImportedNewCars),
            new CarCargoOption(TrainCarType.AutorackBlue, CargoType.ImportedNewCars),
            new CarCargoOption(TrainCarType.AutorackGreen, CargoType.NewCars),
            new CarCargoOption(TrainCarType.AutorackYellow, CargoType.NewCars),
            new CarCargoOption(TrainCarType.BoxcarBrown, CargoType.ElectronicsIskar),
            new CarCargoOption(TrainCarType.BoxcarGreen, CargoType.ElectronicsKrugmann),
            new CarCargoOption(TrainCarType.BoxcarRed, CargoType.ToolsIskar),
            new CarCargoOption(TrainCarType.BoxcarPink, CargoType.Furniture),
            new CarCargoOption(TrainCarType.BoxcarBrown, CargoType.ClothingObco),
            new CarCargoOption(TrainCarType.BoxcarGreen, CargoType.Medicine),
            new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.ScrapContainers),
            new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.EmptySunOmni),
            new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.EmptyIskar),
            new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.EmptyObco),
            new CarCargoOption(TrainCarType.RefrigeratorWhite, CargoType.TropicalFruits)
        };

        // Military Base (Vehicles, ammunition, armor, military supplies)
        private static readonly CarCargoOption[] s_militaryBaseOutbound = new CarCargoOption[]
        {
            new CarCargoOption(TrainCarType.FlatbedMilitary, CargoType.Tanks),
            new CarCargoOption(TrainCarType.FlatbedMilitary, CargoType.MilitaryTrucks),
            new CarCargoOption(TrainCarType.FlatbedMilitary, CargoType.AttackHelicopters),
            new CarCargoOption(TrainCarType.FlatbedMilitary, CargoType.Missiles),
            new CarCargoOption(TrainCarType.FlatbedMilitary, CargoType.MilitaryCars),
            new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.MilitarySupplies),
            new CarCargoOption(TrainCarType.BoxcarMilitary, CargoType.Ammunition),
            new CarCargoOption(TrainCarType.BoxcarBrown, CargoType.MilitarySupplies),
            new CarCargoOption(TrainCarType.TankBlack, CargoType.Diesel)
        };

        // Passenger coaches
        private static readonly TrainCarType[] s_passengerCoaches = new TrainCarType[]
        {
            TrainCarType.PassengerRed,
            TrainCarType.PassengerGreen,
            TrainCarType.PassengerBlue
        };

        #endregion

        /// <summary>
        /// Retrieves the prototypical TrainCarLivery for a given TrainCarType from Derail Valley's object model.
        /// </summary>
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
        /// Generates an industry-aware consist specification containing both car liveries and loaded cargo types,
        /// sized according to locomotive tractive ratings and cargo mass.
        /// </summary>
        /// <param name="type">The consist tier/configuration.</param>
        /// <param name="originYard">Origin station yard ID (e.g. "SM", "CM", "HB").</param>
        /// <param name="destYard">Destination station yard ID (e.g. "MF", "GF", "SW").</param>
        /// <param name="rng">Random number generator.</param>
        /// <returns>A list of ConsistCarSpec instances starting with the lead locomotive(s).</returns>
        public static List<ConsistCarSpec> GetConsistSpecs(ConsistType type, string originYard = null, string destYard = null, System.Random rng = null)
        {
            if (rng == null) rng = s_defaultRng;
            List<ConsistCarSpec> consist = new List<ConsistCarSpec>();

            string oYard = (originYard ?? "").ToUpperInvariant();
            string dYard = (destYard ?? "").ToUpperInvariant();

            switch (type)
            {
                case ConsistType.PassengerCommuter:
                    BuildPassengerCommuterConsist(consist, rng);
                    break;

                case ConsistType.ShunterFreight:
                    BuildShunterConsist(consist, oYard, dYard, rng);
                    break;

                case ConsistType.RegionalFreight:
                    BuildRegionalConsist(consist, oYard, dYard, rng);
                    break;

                case ConsistType.MainlineHeavy:
                    BuildMainlineHeavyConsist(consist, oYard, dYard, rng);
                    break;

                default:
                    BuildRegionalConsist(consist, oYard, dYard, rng);
                    break;
            }

            // Defensive check: Ensure at least one locomotive exists
            if (consist.Count == 0 || consist[0].Livery == null)
            {
                consist.Insert(0, new ConsistCarSpec(TrainCarType.LocoShunter, CargoType.None));
            }

            return consist;
        }

        /// <summary>
        /// Backwards-compatible overload generating a list of liveries.
        /// </summary>
        public static List<TrainCarLivery> GetLiveries(ConsistType type, System.Random rng)
        {
            var specs = GetConsistSpecs(type, null, null, rng);
            List<TrainCarLivery> liveries = new List<TrainCarLivery>(specs.Count);
            for (int i = 0; i < specs.Count; i++)
            {
                if (specs[i].Livery != null)
                {
                    liveries.Add(specs[i].Livery);
                }
            }
            return liveries;
        }

        public static List<TrainCarLivery> GetLiveries(ConsistType type)
        {
            return GetLiveries(type, s_defaultRng);
        }

        #region Consist Builders & Industry Resolvers

        private static void BuildPassengerCommuterConsist(List<ConsistCarSpec> result, System.Random rng)
        {
            // Lead Locomotive: 50% DM3, 40% DE2, 10% DH4
            double locoChoice = rng.NextDouble();
            TrainCarType loco = (locoChoice < 0.50) ? TrainCarType.LocoDM3 : (locoChoice < 0.90 ? TrainCarType.LocoShunter : TrainCarType.LocoDH4);
            AddCar(result, loco, CargoType.None);

            // 2 to 4 passenger coaches
            int coachCount = rng.Next(2, 5);
            for (int i = 0; i < coachCount; i++)
            {
                TrainCarType coach = s_passengerCoaches[rng.Next(0, s_passengerCoaches.Length)];
                AddCar(result, coach, CargoType.None);
            }
        }

        private static void BuildShunterConsist(List<ConsistCarSpec> result, string oYard, string dYard, System.Random rng)
        {
            // Lead Locomotive: DE2 (LocoShunter) or DM3
            TrainCarType loco = (rng.NextDouble() < 0.35) ? TrainCarType.LocoDM3 : TrainCarType.LocoShunter;
            AddCar(result, loco, CargoType.None);

            // Determine industry theme and cargo
            List<CarCargoOption> freightPool;
            bool isHeavyBulk;
            bool isEmptyReturn;
            ResolveIndustryCorridor(oYard, dYard, rng, out freightPool, out isHeavyBulk, out isEmptyReturn);

            // Shunter Tonnage Limits (DE2 rating ~250-320t):
            // Loaded Heavy Bulk: 3 to 4 cars (~220-280t)
            // Loaded Light/Medium Freight: 4 to 6 cars (~160-250t)
            // Empty Freight: 5 to 7 cars (~110-170t)
            int carCount;
            if (isEmptyReturn)
            {
                carCount = rng.Next(5, 8);
            }
            else if (isHeavyBulk)
            {
                carCount = rng.Next(3, 5);
            }
            else
            {
                carCount = rng.Next(4, 7);
            }

            for (int i = 0; i < carCount; i++)
            {
                var opt = freightPool[rng.Next(0, freightPool.Count)];
                CargoType cargoToLoad = isEmptyReturn ? CargoType.None : opt.Cargo;
                AddCar(result, opt.CarType, cargoToLoad);
            }

            // 50% chance of Caboose on rear
            if (rng.NextDouble() > 0.5)
            {
                AddCar(result, TrainCarType.CabooseRed, CargoType.None);
            }
        }

        private static void BuildRegionalConsist(List<ConsistCarSpec> result, string oYard, string dYard, System.Random rng)
        {
            // Lead Locomotive: DH4 (or occasional double DE2 for local transfers)
            AddCar(result, TrainCarType.LocoDH4, CargoType.None);

            List<CarCargoOption> freightPool;
            bool isHeavyBulk;
            bool isEmptyReturn;
            ResolveIndustryCorridor(oYard, dYard, rng, out freightPool, out isHeavyBulk, out isEmptyReturn);

            // DH4 Tonnage Limits (rating ~600-800t on grades):
            // Loaded Heavy Bulk: 6 to 8 cars (~450-620t)
            // Loaded Medium/Light Freight: 8 to 11 cars (~350-520t)
            // Empty Freight: 10 to 14 cars (~220-310t)
            int carCount;
            if (isEmptyReturn)
            {
                carCount = rng.Next(10, 15);
            }
            else if (isHeavyBulk)
            {
                carCount = rng.Next(6, 9);
            }
            else
            {
                carCount = rng.Next(8, 12);
            }

            for (int i = 0; i < carCount; i++)
            {
                var opt = freightPool[rng.Next(0, freightPool.Count)];
                CargoType cargoToLoad = isEmptyReturn ? CargoType.None : opt.Cargo;
                AddCar(result, opt.CarType, cargoToLoad);
            }

            // Rear Caboose
            AddCar(result, TrainCarType.CabooseRed, CargoType.None);
        }

        private static void BuildMainlineHeavyConsist(List<ConsistCarSpec> result, string oYard, string dYard, System.Random rng)
        {
            List<CarCargoOption> freightPool;
            bool isHeavyBulk;
            bool isEmptyReturn;
            ResolveIndustryCorridor(oYard, dYard, rng, out freightPool, out isHeavyBulk, out isEmptyReturn);

            // Double-heading DE6 for heavy trains (35% chance overall, or 60% chance if heavy bulk loaded)
            bool isDoubleHeader = isHeavyBulk ? (rng.NextDouble() < 0.60) : (rng.NextDouble() < 0.35);

            AddCar(result, TrainCarType.LocoDiesel, CargoType.None);
            if (isDoubleHeader)
            {
                AddCar(result, TrainCarType.LocoDiesel, CargoType.None);
            }

            // DE6 Tonnage Limits:
            // Single DE6 (rating ~1,000-1,200t):
            //   - Loaded Heavy Bulk: 10 to 13 cars (~750-1,000t)
            //   - Loaded Medium/Mixed: 12 to 16 cars (~550-800t)
            //   - Empty return: 14 to 18 cars (~320-420t)
            // Double DE6 (rating ~2,000-2,400t):
            //   - Loaded Heavy Bulk: 16 to 22 cars (~1,200-1,700t)
            //   - Loaded Medium/Mixed: 18 to 24 cars (~800-1,150t)
            //   - Empty return: 20 to 28 cars (~450-650t)
            int carCount;
            if (isDoubleHeader)
            {
                if (isEmptyReturn)
                    carCount = rng.Next(20, 29);
                else if (isHeavyBulk)
                    carCount = rng.Next(16, 23);
                else
                    carCount = rng.Next(18, 25);
            }
            else
            {
                if (isEmptyReturn)
                    carCount = rng.Next(14, 19);
                else if (isHeavyBulk)
                    carCount = rng.Next(10, 14);
                else
                    carCount = rng.Next(12, 17);
            }

            for (int i = 0; i < carCount; i++)
            {
                var opt = freightPool[rng.Next(0, freightPool.Count)];
                CargoType cargoToLoad = isEmptyReturn ? CargoType.None : opt.Cargo;
                AddCar(result, opt.CarType, cargoToLoad);
            }

            // Rear Caboose
            AddCar(result, TrainCarType.CabooseRed, CargoType.None);
        }

        /// <summary>
        /// Analyzes origin and destination station codes to construct an industry-appropriate freight pool,
        /// determine heavy bulk categorization, and decide whether the run is an empty return consist.
        /// </summary>
        private static void ResolveIndustryCorridor(
            string oYard, string dYard, System.Random rng,
            out List<CarCargoOption> freightPool, out bool isHeavyBulk, out bool isEmptyReturn)
        {
            freightPool = new List<CarCargoOption>();
            isHeavyBulk = false;
            isEmptyReturn = false;

            // 1. Coal Mine Chains (CM)
            if (oYard.Contains("CM"))
            {
                // Outbound coal from mine to Steel Mill, Harbor, Goods Factory
                isHeavyBulk = true;
                isEmptyReturn = false;
                for (int i = 0; i < s_coalHoppers.Length; i++)
                    freightPool.Add(new CarCargoOption(s_coalHoppers[i], CargoType.Coal));
                return;
            }
            if (dYard.Contains("CM"))
            {
                // Heading to Coal Mine: 75% empty hoppers returning to load, 25% mine machinery/supplies
                isHeavyBulk = true;
                if (rng.NextDouble() < 0.75)
                {
                    isEmptyReturn = true;
                    for (int i = 0; i < s_coalHoppers.Length; i++)
                        freightPool.Add(new CarCargoOption(s_coalHoppers[i], CargoType.None));
                }
                else
                {
                    isEmptyReturn = false;
                    freightPool.Add(new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.Pipes));
                    freightPool.Add(new CarCargoOption(TrainCarType.FlatbedShort, CargoType.ToolsBrohm));
                    freightPool.Add(new CarCargoOption(TrainCarType.BoxcarBrown, CargoType.ToolsIskar));
                }
                return;
            }

            // 2. Iron Ore Mine Chains (IME, IMW)
            if (oYard.Contains("IM"))
            {
                // Outbound iron ore from mines to Steel Mill, Harbor
                isHeavyBulk = true;
                isEmptyReturn = false;
                for (int i = 0; i < s_ironOreHoppers.Length; i++)
                    freightPool.Add(new CarCargoOption(s_ironOreHoppers[i], CargoType.IronOre));
                return;
            }
            if (dYard.Contains("IM"))
            {
                // Heading to Iron Ore Mine: 80% empty hoppers returning to load, 20% mining machinery
                isHeavyBulk = true;
                if (rng.NextDouble() < 0.80)
                {
                    isEmptyReturn = true;
                    for (int i = 0; i < s_ironOreHoppers.Length; i++)
                        freightPool.Add(new CarCargoOption(s_ironOreHoppers[i], CargoType.None));
                }
                else
                {
                    isEmptyReturn = false;
                    freightPool.Add(new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.MiningTrucks));
                    freightPool.Add(new CarCargoOption(TrainCarType.FlatbedShort, CargoType.ToolsBrohm));
                    freightPool.Add(new CarCargoOption(TrainCarType.BoxcarBrown, CargoType.ToolsIskar));
                }
                return;
            }

            // 3. Sawmill Chains (SW)
            if (dYard.Contains("SW"))
            {
                // Heading to Sawmill: logs on flatbed stakes or empty logging flatcars
                if (oYard.Contains("FM") || oYard.Contains("FR") || rng.NextDouble() < 0.70)
                {
                    freightPool.Add(new CarCargoOption(TrainCarType.FlatbedStakes, CargoType.Logs));
                }
                else
                {
                    isEmptyReturn = true;
                    freightPool.Add(new CarCargoOption(TrainCarType.FlatbedStakes, CargoType.None));
                }
                return;
            }
            if (oYard.Contains("SW"))
            {
                // Outbound lumber products from Sawmill
                freightPool.AddRange(s_sawmillOutbound);
                return;
            }

            // 4. Steel Mill Chains (SM)
            if (oYard.Contains("SM"))
            {
                // Outbound steel products from Steel Mill
                isHeavyBulk = true;
                freightPool.AddRange(s_steelMillOutbound);
                return;
            }
            if (dYard.Contains("SM"))
            {
                // Inbound scrap metal / crude oil / empty flatbeds heading to Steel Mill
                isHeavyBulk = true;
                if (rng.NextDouble() < 0.30)
                {
                    isEmptyReturn = true;
                    freightPool.Add(new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.None));
                    freightPool.Add(new CarCargoOption(TrainCarType.GondolaGreen, CargoType.None));
                }
                else
                {
                    freightPool.AddRange(s_steelMillInboundScrap);
                }
                return;
            }

            // 5. Oil Extraction & Refining Chains (OWN, OWC)
            if (oYard.Contains("OW") || dYard.Contains("OW"))
            {
                isHeavyBulk = true;
                if (dYard.Contains("OWN") && rng.NextDouble() < 0.65)
                {
                    // Empty tankers returning to oil extraction wells
                    isEmptyReturn = true;
                    freightPool.Add(new CarCargoOption(TrainCarType.TankBlack, CargoType.None));
                    freightPool.Add(new CarCargoOption(TrainCarType.TankOrange, CargoType.None));
                    freightPool.Add(new CarCargoOption(TrainCarType.TankBlue, CargoType.None));
                }
                else if (oYard.Contains("OWN") && dYard.Contains("OWC"))
                {
                    // Crude oil transfer from North wells to Central refinery
                    freightPool.Add(new CarCargoOption(TrainCarType.TankBlack, CargoType.CrudeOil));
                    freightPool.Add(new CarCargoOption(TrainCarType.TankOrange, CargoType.CrudeOil));
                }
                else
                {
                    // Refined fuels & chemicals outbound from refinery
                    freightPool.AddRange(s_oilRefineryOutbound);
                }
                return;
            }

            // 6. Food & Agriculture Chains (FM, FR, FF)
            if (oYard.Contains("FM") || oYard.Contains("FR"))
            {
                // Farm / Forest Meadow outbound crops, livestock, milk
                freightPool.AddRange(s_farmOutbound);
                return;
            }
            if (oYard.Contains("FF"))
            {
                // Food Factory outbound packaged food, meat, dairy
                freightPool.AddRange(s_foodFactoryOutbound);
                return;
            }
            if (dYard.Contains("FF"))
            {
                // Inbound crops, milk, livestock heading to Food Factory
                freightPool.AddRange(s_farmOutbound);
                return;
            }
            if (dYard.Contains("FM") || dYard.Contains("FR"))
            {
                // Supplies / tractors heading to Farm
                if (rng.NextDouble() < 0.40)
                {
                    isEmptyReturn = true;
                    freightPool.Add(new CarCargoOption(TrainCarType.StockBrown, CargoType.None));
                    freightPool.Add(new CarCargoOption(TrainCarType.HopperBrown, CargoType.None));
                }
                else
                {
                    freightPool.Add(new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.Tractors));
                    freightPool.Add(new CarCargoOption(TrainCarType.BoxcarBrown, CargoType.ToolsIskar));
                }
                return;
            }

            // 7. Machine Factory Chains (MF)
            if (oYard.Contains("MF"))
            {
                // Machine Factory outbound vehicles, construction machines, tools
                freightPool.AddRange(s_machineFactoryOutbound);
                return;
            }

            // 8. Military Base Chains (MB)
            if (oYard.Contains("MB") || dYard.Contains("MB"))
            {
                freightPool.AddRange(s_militaryBaseOutbound);
                return;
            }

            // 9. Harbor & Goods Factory Intermodal Chains (HB, GF)
            if (oYard.Contains("HB") || oYard.Contains("GF") || dYard.Contains("HB") || dYard.Contains("GF"))
            {
                freightPool.AddRange(s_goodsAndHarborOutbound);
                return;
            }

            // 10. General Valley Mixed Freight Fallback
            if (rng.NextDouble() < 0.25)
            {
                // Empty mixed return
                isEmptyReturn = true;
                freightPool.Add(new CarCargoOption(TrainCarType.BoxcarBrown, CargoType.None));
                freightPool.Add(new CarCargoOption(TrainCarType.FlatbedEmpty, CargoType.None));
                freightPool.Add(new CarCargoOption(TrainCarType.TankBlue, CargoType.None));
                freightPool.Add(new CarCargoOption(TrainCarType.GondolaGreen, CargoType.None));
            }
            else
            {
                freightPool.AddRange(s_goodsAndHarborOutbound);
                freightPool.AddRange(s_foodFactoryOutbound);
                freightPool.AddRange(s_machineFactoryOutbound);
            }
        }

        private static void AddCar(List<ConsistCarSpec> list, TrainCarType carType, CargoType cargo)
        {
            TrainCarLivery livery = GetLivery(carType);
            if (livery != null)
            {
                list.Add(new ConsistCarSpec(livery, cargo));
            }
        }

        #endregion
    }
}

