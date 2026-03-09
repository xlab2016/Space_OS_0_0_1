using Magic.Kernel.Space;
using Magic.Kernel.Devices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Magic.Kernel.Functions
{
    public static class MathFunctions
    {
        /// <summary>
        /// Загружает вершины из shape. Если у shape есть VertexIndices, загружает вершины по индексам.
        /// Если есть Vertices, возвращает их. Если есть и то и другое, объединяет.
        /// </summary>
        public static async Task<List<Vertex>> LoadVerticesAsync(Shape shape, ISpaceDisk? disk, string? spaceName = null)
        {
            var vertices = new List<Vertex>();

            // Если есть уже загруженные вершины, добавляем их
            if (shape.Vertices != null && shape.Vertices.Count > 0)
            {
                vertices.AddRange(shape.Vertices);
            }

            // Если есть индексы вершин, загружаем их
            if (shape.VertexIndices != null && shape.VertexIndices.Count > 0 && disk != null)
            {
                foreach (var vertexIndex in shape.VertexIndices)
                {
                    var vertex = await disk.GetVertex(vertexIndex, null, spaceName);
                    if (vertex != null)
                    {
                        // Проверяем, нет ли уже такой вершины (по индексу)
                        if (!vertices.Any(v => v.Index.HasValue && v.Index.Value == vertexIndex))
                        {
                            vertices.Add(vertex);
                        }
                    }
                }
            }

            return vertices;
        }
        public static Position CalculateCenterOfMass(List<Position> positions)
        {
            if (positions.Count == 0)
            {
                return new Position { Dimensions = new List<float>() };
            }

            // Определяем максимальную размерность
            var maxDimensions = positions.Max(p => p.Dimensions?.Count ?? 0);
            if (maxDimensions == 0)
            {
                return new Position { Dimensions = new List<float>() };
            }

            // Вычисляем среднее по каждой размерности
            var centerDimensions = new List<float>(maxDimensions);
            for (int i = 0; i < maxDimensions; i++)
            {
                float sum = 0;
                int count = 0;
                foreach (var pos in positions)
                {
                    if (pos.Dimensions != null && i < pos.Dimensions.Count)
                    {
                        sum += pos.Dimensions[i];
                        count++;
                    }
                }
                centerDimensions.Add(count > 0 ? sum / count : 0);
            }

            return new Position { Dimensions = centerDimensions };
        }

        public static Position CalculatePositionIntersection(Position posA, Position posB)
        {
            var maxDimensions = Math.Max(
                posA.Dimensions?.Count ?? 0,
                posB.Dimensions?.Count ?? 0
            );

            var intersectionDimensions = new List<float>(maxDimensions);
            for (int i = 0; i < maxDimensions; i++)
            {
                float valA = 0;
                float valB = 0;

                if (posA.Dimensions != null && i < posA.Dimensions.Count)
                {
                    valA = posA.Dimensions[i];
                }
                if (posB.Dimensions != null && i < posB.Dimensions.Count)
                {
                    valB = posB.Dimensions[i];
                }

                // Пересечение = среднее значение (можно изменить логику)
                intersectionDimensions.Add((valA + valB) / 2);
            }

            return new Position { Dimensions = intersectionDimensions };
        }

        public static async Task<Shape> CalculateIntersectionAsync(Shape shapeA, Shape shapeB, ISpaceDisk? disk = null, string? spaceName = null)
        {
            // Создаем новую shape для результата пересечения
            var intersection = new Shape
            {
                Name = $"Intersection({shapeA.Name ?? "A"}, {shapeB.Name ?? "B"})"
            };

            // Если у обеих shapes есть Origin, вычисляем пересечение позиций
            if (shapeA.Origin != null && shapeB.Origin != null)
            {
                var intersectionOrigin = CalculatePositionIntersection(shapeA.Origin, shapeB.Origin);
                intersection.Origin = intersectionOrigin;
            }
            else if (shapeA.Position != null && shapeB.Position != null)
            {
                var intersectionPosition = CalculatePositionIntersection(shapeA.Position, shapeB.Position);
                intersection.Position = intersectionPosition;
            }

            // Загружаем вершины из обеих shapes (включая загрузку по индексам)
            var verticesA = await LoadVerticesAsync(shapeA, disk, spaceName);
            var verticesB = await LoadVerticesAsync(shapeB, disk, spaceName);

            // Находим общие вершины
            if (verticesA.Count > 0 && verticesB.Count > 0)
            {
                var commonVertices = new List<Vertex>();
                foreach (var vertexA in verticesA)
                {
                    foreach (var vertexB in verticesB)
                    {
                        if (AreVerticesEqual(vertexA, vertexB))
                        {
                            commonVertices.Add(vertexA);
                            break;
                        }
                    }
                }
                if (commonVertices.Count > 0)
                {
                    intersection.Vertices = commonVertices;
                }
            }

            return intersection;
        }

        public static bool AreVerticesEqual(Vertex vertexA, Vertex vertexB)
        {
            // Сравниваем по Position
            if (vertexA.Position != null && vertexB.Position != null)
            {
                if (vertexA.Position.Dimensions == null || vertexB.Position.Dimensions == null)
                {
                    return false;
                }

                if (vertexA.Position.Dimensions.Count != vertexB.Position.Dimensions.Count)
                {
                    return false;
                }

                for (int i = 0; i < vertexA.Position.Dimensions.Count; i++)
                {
                    if (Math.Abs(vertexA.Position.Dimensions[i] - vertexB.Position.Dimensions[i]) > 0.0001f)
                    {
                        return false;
                    }
                }
                return true;
            }

            // Сравниваем по Index
            if (vertexA.Index.HasValue && vertexB.Index.HasValue)
            {
                return vertexA.Index.Value == vertexB.Index.Value;
            }

            return false;
        }
    }
}
