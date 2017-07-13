﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using ASELib;
using System.IO;

namespace MethylationDistribution
{
	class Program
	{

		// keeps track of the tumor and normal methylation distributions
		static Dictionary<double, int> tumorMethylationDistribution = new Dictionary<double, int>();
		static Dictionary<double, int> normalMethylationDistribution = new Dictionary<double, int>();

		static Dictionary<string, List<MethylationPoint[]>> elements = new Dictionary<string, List<MethylationPoint[]>>();

		static void ProcessCases(List<ASETools.Case> cases, List<string> selectedHugoSymbols)
		{
			while (true)
			{
				ASETools.Case case_;
				lock (cases)
				{
					if (cases.Count() == 0)
					{
						//
						// No more work, we're done.
						//
						return;
					}

					case_ = cases[0];

					cases.RemoveAt(0);
				}

				// Read in methylation values and ASE values
				if (case_.tumor_allele_specific_gene_expression_filename == "" || case_.tumor_regional_methylation_filename == "")
				{
					// no data. continue
					continue;
				}

				var regionalMethylationData = ASETools.RegionalSignalFile.ReadFile(case_.tumor_regional_methylation_filename);

				// TODO write file header


				foreach (var hugoSymbol in selectedHugoSymbols)
				{
					// Check if the mutation counts for this gene and case id exist
					Dictionary<string, int> mutationCountsForThisGene;
					int mutationCount;
					if (mutationCounts.TryGetValue(hugoSymbol, out mutationCountsForThisGene))
					{
						if (!mutationCountsForThisGene.TryGetValue(case_.case_id, out mutationCount))
						{
							continue;
						}

					}
					else
					{
						continue;
					}

					// TODO check for value
					var methylationForGene = regionalMethylationData.Item1[hugoSymbol];

					if (methylationForGene.Count() == 0)
					{
						continue;
					}

					// Add to elements
					MethylationPoint[] array = methylationForGene.Select(r => new MethylationPoint(hugoSymbol, r, mutationCount == 1))
						.ToArray();

					lock (elements)
					{
						// If gene does not exist 
						List<MethylationPoint[]> element;
						if (!elements.TryGetValue(hugoSymbol, out element))
						{
							elements.Add(hugoSymbol, new List<MethylationPoint[]>());
						}
						elements[hugoSymbol].Add(array);
					}
				}

			}
		}

		// TODO consolidate all these duplicated functions
		public class MethylationPoint : IComparer<MethylationPoint>
		{
			public string identifier;
			public double adjustedBValue;
			public double mValue;
			public bool hasOneMutation;

		public MethylationPoint(string identifier_, double mValue_, bool hasOneMutation_)
			{
				mValue = mValue_;
				identifier = identifier_;
				hasOneMutation = hasOneMutation_;

				var bValue = ASETools.AnnotationLine.M2Beta(mValue_);
				adjustedBValue = Math.Abs(2 * bValue - 1); // hemimethylated are 0, full and none are 1 TODO reverse this
			}

			public int Compare(MethylationPoint a, MethylationPoint b)
			{
				return xCompare(a, b);
			}

			static public int xCompare(MethylationPoint a, MethylationPoint b)
			{
				if (a.adjustedBValue > b.adjustedBValue) return 1;
				if (a.adjustedBValue < b.adjustedBValue) return -1;
				return 0;
			}
		}

		static bool headerWritten = false; // TODO SO LAZY...
		static StreamWriter panCancerOutputFile;

		// stores distance for lowest pvalue
		static Dictionary<string, Tuple<string, double>> bestPValues = new Dictionary<string, Tuple<string, double>>();

		// Dictionary of hugoSymbol, then dictionary of (case_id, mutationCount)
		static Dictionary<string, Dictionary<string, int>> mutationCounts = new Dictionary<string, Dictionary<string, int>>();

		// Dictionary of hugoSymbol, then dictionare of (case_id, mutationCount)
		static Dictionary<string, Dictionary<string, double[]>> methylationValues = new Dictionary<string, Dictionary<string, double[]>>();

		static void readFinalValues(string filename)
		{

			var modValue = 11;

			// TODO move to bonferroni corrected values once available
			List<string[]> lines = ASETools.ReadAllLinesWithRetry(filename).Select(r => r.Split('\t')).ToList();

			var headers = lines[0];

			for (var i = 1; i < lines.Count(); i++)
			{
				var hugoSymbol = lines[i][0];

				// get p-values for 1 vs not 1

				var pvalues = lines[i].Select((value, index) => new Tuple<string, int>(value, index))
					.Where(r => (r.Item2 - 2) % modValue == 0)
					.Where(r => r.Item1 != "*").Select(r => new Tuple<double, int>(Convert.ToDouble(r.Item1), r.Item2));

				if (pvalues.Count() == 0)
				{
					continue;
				}

				var min = pvalues.Min(obj => obj.Item1);

				var bestPValue = pvalues.Where(r => r.Item1 == min).First();

				var label = headers[bestPValue.Item2];
				var distance = label.Substring(0, label.IndexOf(" 1 vs. not 1"));
				bestPValues.Add(hugoSymbol, new Tuple<string, double>(distance, min));
			}
		}

		static void loadMutationCounts(List<ASETools.Case> cases)
		{
			foreach (var case_ in cases)
			{
				var aseFile = ASETools.RegionalSignalFile.ReadFile(case_.tumor_allele_specific_gene_expression_filename);

				foreach (var hugoSymbol in bestPValues.Keys)
				{
					double[] hugoData;

					if (!aseFile.Item1.TryGetValue(ASETools.ConvertToExcelString(hugoSymbol), out hugoData))
					{
						continue;
					}

					// get number of non-silent mutations
					var mutationCount = aseFile.Item3[ASETools.ConvertToExcelString(hugoSymbol)];
					if (!mutationCounts.ContainsKey(hugoSymbol))
					{
						mutationCounts.Add(hugoSymbol, new Dictionary<string, int>());
					}

					mutationCounts[hugoSymbol].Add(case_.case_id, mutationCount);
				}
			}
		}

		static void Main(string[] args)
		{
			var configuration = ASETools.ASEConfirguation.loadFromFile(args);
			var cases = ASETools.Case.LoadCases(configuration.casesFilePathname).Take(20);

			// Case 1: for any group of cases, compute the pvalues for adjusted beta value.
			// Run Mann Whitney for test for ASM values from adjusted beta value where the groups are 
			// ase vs no ase. (per gene) The purpose of this is to see if ASM significantly explains ASE for any locations.

			var regionsToProcess = 66;


			// load in known genes
			var knownGenes = ASETools.readKnownGeneFile(ASETools.ASEConfirguation.defaultGeneLocationInformation);

			string baseFileName = configuration.finalResultsDirectory + "methylationResults.allSites.txt";

			panCancerOutputFile = ASETools.CreateStreamWriterWithRetry(baseFileName);

			var pValueFile = configuration.finalResultsDirectory + "AlleleSpecificExpressionDistributionByMutationCount.txt";
			readFinalValues(pValueFile);

			// Select only hugo symbols with low pvalues
			var selectedHugoSymbols = bestPValues.Where(r => r.Value.Item2 < 1.0E-6).Select(r => r.Key);

			// load the mutations for each file
			loadMutationCounts(cases.Select(r => r.Value).ToList());

			ASETools.MannWhitney<MethylationPoint>.WhichGroup whichGroup = new ASETools.MannWhitney<MethylationPoint>.WhichGroup(m => m.hasOneMutation);
			ASETools.MannWhitney<MethylationPoint>.GetValue getValue = new ASETools.MannWhitney<MethylationPoint>.GetValue(x => x.adjustedBValue);
			bool twoTailed = true;

			// Preprocess cases by putting all required values in methylationValues
			var threads = new List<Thread>();
			var selectedCases = cases.Select(r => r.Value).ToList();

			for (int i = 0; i < Environment.ProcessorCount; i++)
			{
				threads.Add(new Thread(() => ProcessCases(selectedCases, selectedHugoSymbols.ToList())));
			}

			threads.ForEach(th => th.Start());
			threads.ForEach(th => th.Join());


			foreach (var hugoSymbol in selectedHugoSymbols)
			{
				Dictionary<string, int> mutationCountsForThisGene;
				if (mutationCounts.TryGetValue(hugoSymbol, out mutationCountsForThisGene))
				{
					continue;
				}

				var bestPValue = bestPValues[hugoSymbol];

				ASETools.GeneLocationInfo knownGene;

				if (!knownGenes.TryGetValue(ASETools.ConvertToNonExcelString(hugoSymbol), out knownGene))
				{
					continue;
				}

				var hugoLine = hugoSymbol + "\t" + bestPValue.Item2 + "\t" + bestPValue.Item1;

				// If elements has no items, skip this hugo symbol
				if (elements.Count() == 0)
				{
					hugoLine += string.Concat(Enumerable.Repeat("\t*", regionsToProcess));
					panCancerOutputFile.WriteLine(hugoLine);
					continue;
				}

				// Iterate through all regions
				for (var i = 0; i < regionsToProcess; i++)
				{
					bool reversed;
					bool enoughData;
					double nFirstGroup;
					double nSecondGroup;
					double U;
					double z;

					var forRegion = elements[hugoSymbol].Select(r => r[i]).Where(r => r.mValue > Double.NegativeInfinity).ToList(); // Filter by neg infinity
					if (forRegion.Count() > 0)
					{
						var p = ASETools.MannWhitney<MethylationPoint>.ComputeMannWhitney(forRegion, 
							forRegion[0], whichGroup, getValue, out enoughData, out reversed, 
							out nFirstGroup, out nSecondGroup, out U, out z, twoTailed, 20);
						if (!enoughData)
						{
							hugoLine += "\t*";
						}
						else
						{
							hugoLine += "\t" + p;
						}
					}
					else {
						hugoLine += "\t*";
					}

				} // foreach region

				panCancerOutputFile.WriteLine(hugoLine);
				panCancerOutputFile.Flush();

			} // foreach hugo symbol 

			panCancerOutputFile.Close();


			// Case 2: Run t test, trying to assign distributions for methylation points. This is to only
			// look at 1 group (ie. ase) Thie purpose of this is to see what percentage of ASE significant
			// results can be explained by ASE. This should be written generically enough so it can
			// also be used to test no ASE for 0 mutations, testing what percentage of these cases fall into the
			// 'full methylation' category

		}
	}
}
