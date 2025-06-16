using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Google.OrTools.Sat;

namespace TSPlayground;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private List<Point> cities = new List<Point>();
    private List<int> solution = new List<int>();
    private Random random = new Random();
    private const double CITY_RADIUS = 3;
    private const double MARGIN = 50;
    private const double LABEL_OFFSET = 10;

    public MainWindow()
    {
        InitializeComponent();
        GenerateButton.Click += GenerateButton_Click;
        SolveButton.Click += SolveButton_Click;
    }

    private void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CityCountTextBox.Text, out int cityCount) || cityCount < 5 || cityCount > 1000)
        {
            MessageBox.Show("Please enter a valid number of cities between 5 and 1000.");
            return;
        }

        cities.Clear();
        solution.Clear();
        VisualizationCanvas.Children.Clear();

        // Generate random cities
        for (int i = 0; i < cityCount; i++)
        {
            double x = random.NextDouble() * (VisualizationCanvas.ActualWidth - 40) + 20;
            double y = random.NextDouble() * (VisualizationCanvas.ActualHeight - 40) + 20;
            cities.Add(new Point(x, y));
        }

        DrawCities();
    }

    private void SolveButton_Click(object sender, RoutedEventArgs e)
    {
        if (cities.Count == 0)
        {
            MessageBox.Show("Please generate cities first.");
            return;
        }

        if (!double.TryParse(OptimalityGapTextBox.Text, out double optimalityGap) || 
            optimalityGap < 0 || optimalityGap > 20)
        {
            MessageBox.Show("Please enter a valid optimality gap between 0 and 20.");
            return;
        }

        if (!int.TryParse(TimeoutTextBox.Text, out int timeout) || timeout <= 0)
        {
            MessageBox.Show("Please enter a valid timeout in seconds.");
            return;
        }

        // Parse cuts
        var cuts = new List<(int, int)>();
        if (!string.IsNullOrWhiteSpace(CutsTextBox.Text))
        {
            var cutPairs = CutsTextBox.Text.Split(',');
            foreach (var pair in cutPairs)
            {
                var cities = pair.Trim().Split('-');
                if (cities.Length == 2 && 
                    int.TryParse(cities[0], out int city1) && 
                    int.TryParse(cities[1], out int city2))
                {
                    cuts.Add((city1, city2));
                }
            }
        }

        // Clear previous solution and redraw cities
        solution.Clear();
        VisualizationCanvas.Children.Clear();
        DrawCities();

        SolveTSP(optimalityGap, timeout, cuts);
    }

    private void SolveTSP(double optimalityGap, int timeout, List<(int, int)> cuts)
    {
        var model = new CpModel();
        var numCities = cities.Count;

        // Create variables
        var x = new IntVar[numCities, numCities];
        for (int i = 0; i < numCities; i++)
        {
            for (int j = 0; j < numCities; j++)
            {
                if (i != j)
                {
                    x[i, j] = model.NewIntVar(0, 1, $"x_{i}_{j}");
                }
            }
        }

        // Create position variables for subtour elimination
        var u = new IntVar[numCities];
        for (int i = 0; i < numCities; i++)
        {
            u[i] = model.NewIntVar(0, numCities - 1, $"u_{i}");
        }

        // Objective: minimize total distance
        var objective = new LinearExprBuilder();
        for (int i = 0; i < numCities; i++)
        {
            for (int j = 0; j < numCities; j++)
            {
                if (i != j)
                {
                    double distance = CalculateDistance(cities[i], cities[j]);
                    objective.AddTerm(x[i, j], (long)(distance * 1000)); // Scale up to avoid floating point issues
                }
            }
        }
        model.Minimize(objective);

        // Constraints
        // Each city must be visited exactly once
        for (int i = 0; i < numCities; i++)
        {
            var rowSum = new LinearExprBuilder();
            var colSum = new LinearExprBuilder();
            for (int j = 0; j < numCities; j++)
            {
                if (i != j)
                {
                    rowSum.Add(x[i, j]);
                    colSum.Add(x[j, i]);
                }
            }
            model.Add(rowSum == 1);
            model.Add(colSum == 1);
        }

        // Subtour elimination constraints (Miller-Tucker-Zemlin formulation)
        for (int i = 1; i < numCities; i++)
        {
            for (int j = 1; j < numCities; j++)
            {
                if (i != j)
                {
                    model.Add(u[i] - u[j] + numCities * x[i, j] <= numCities - 1);
                }
            }
        }

        // Add cuts
        foreach (var (city1, city2) in cuts)
        {
            if (city1 >= 0 && city1 < numCities && city2 >= 0 && city2 < numCities)
            {
                model.Add(x[city1, city2] == 0);
                model.Add(x[city2, city1] == 0);
            }
        }

        // Solve
        var solver = new CpSolver();
        // Set timeout
        solver.StringParameters = $"max_time_in_seconds:{timeout:F2}";

        // Solve
        var status = solver.Solve(model);
        
        // Build output text
        var output = new System.Text.StringBuilder();
        output.AppendLine($"Solver status: {status}");
        output.AppendLine($"Objective value: {solver.ObjectiveValue / 1000.0:F2}");
        output.AppendLine($"Is optimal: {status == CpSolverStatus.Optimal}");
        output.AppendLine($"Optimality gap: {(solver.ObjectiveValue - solver.BestObjectiveBound) / 1000.0:F2}");
        output.AppendLine($"Time: {solver.WallTime():F3} seconds");
        
        if (cuts.Count > 0)
        {
            output.AppendLine("\nActive cuts:");
            foreach (var (city1, city2) in cuts)
            {
                output.AppendLine($"  {city1}-{city2}");
            }
        }
        
        SolverOutputTextBox.Text = output.ToString();

        if (status == CpSolverStatus.Optimal || status == CpSolverStatus.Feasible)
        {
            solution.Clear();
            int currentCity = 0;
            do
            {
                solution.Add(currentCity);
                for (int j = 0; j < numCities; j++)
                {
                    if (j != currentCity && solver.Value(x[currentCity, j]) > 0.5)
                    {
                        currentCity = j;
                        break;
                    }
                }
            } while (currentCity != 0);

            DrawSolution();
        }
    }

    private double CalculateDistance(Point p1, Point p2)
    {
        return Math.Sqrt(Math.Pow(p2.X - p1.X, 2) + Math.Pow(p2.Y - p1.Y, 2));
    }

    private void DrawCities()
    {
        VisualizationCanvas.Children.Clear();
        foreach (var city in cities)
        {
            var ellipse = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = Brushes.Red,
                Stroke = Brushes.White,
                StrokeThickness = 1
            };

            Canvas.SetLeft(ellipse, city.X - 5);
            Canvas.SetTop(ellipse, city.Y - 5);
            VisualizationCanvas.Children.Add(ellipse);

            // Add city index
            var textBlock = new TextBlock
            {
                Text = cities.IndexOf(city).ToString(),
                Foreground = Brushes.White,
                FontSize = 10
            };

            Canvas.SetLeft(textBlock, city.X + 5);
            Canvas.SetTop(textBlock, city.Y + 5);
            VisualizationCanvas.Children.Add(textBlock);
        }
    }

    private void DrawSolution()
    {
        if (solution.Count == 0) return;

        var polyline = new Polyline
        {
            Stroke = Brushes.Yellow,
            StrokeThickness = 2
        };

        foreach (var cityIndex in solution)
        {
            var city = cities[cityIndex];
            polyline.Points.Add(city);
        }

        // Close the path
        polyline.Points.Add(cities[solution[0]]);

        VisualizationCanvas.Children.Add(polyline);
    }
}