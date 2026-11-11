using Microsoft.Xna.Framework;
using StardewValley;
using xTile.Layers;

namespace FarmAssistant;

/// <summary>一件掉落物的预判：停稳后游戏判断吸附用的位置、会不会沉进水里。</summary>
internal readonly record struct DropForecast(Vector2 Final, bool Sinks);

/// <summary>
/// 预判掉落物停在哪里：拿每块碎片当前的位置和速度，照 <c>Debris.updateChunks</c> 的规则往后推算到停稳——
/// 每刻 x += vx、y −= vy、vy −= 0.4（重力）；落到地面线时弹起，vy 变为 2/3、vx 减半，弹过 2 次就停；
/// 碰到墙（Buildings 层不可通行或没有地面）水平速度反向一次；落在会吞掉落物的格子（水等）上就没了。
/// 只读游戏状态，不改动任何东西。一棵树几十块碎片 × 约 100 刻，几千次加减。
/// </summary>
internal static class DropPredictor
{
    private const int MaxTicks = 240;

    public static DropForecast Predict(Debris d, GameLocation loc)
    {
        var chunks = d.Chunks;
        int n = chunks.Count;
        if (n == 0)
            return new DropForecast(Vector2.Zero, true);

        var x = new float[n];
        var y = new float[n];
        var vx = new float[n];
        var vy = new float[n];
        var bounces = new int[n];
        var passed = new bool[n];
        var hitWall = new bool[n];
        var alive = new bool[n];
        for (int i = 0; i < n; i++)
        {
            var c = chunks[i];
            x[i] = c.position.X;
            y[i] = c.position.Y;
            vx[i] = c.xVelocity.Value;
            vy[i] = c.yVelocity.Value;
            bounces[i] = c.bounces;
            passed[i] = c.hasPassedRestingLineOnce.Value;
            hitWall[i] = c.hitWall;
            alive[i] = true;
        }
        int finalY = d.chunkFinalYLevel;
        int finalTarget = d.chunkFinalYTarget;
        bool movingFinal = d.movingFinalYLevel;
        var buildings = loc.Map.GetLayer("Buildings");
        var back = loc.Map.GetLayer("Back");

        for (int t = 0; t < MaxTicks; t++)
        {
            bool moving = false;
            for (int i = n - 1; i >= 0; i--)        // 与游戏相同：从后往前更新
            {
                if (!alive[i] || bounces[i] > 2)
                    continue;
                moving = true;
                x[i] += vx[i];
                y[i] -= vy[i];
                if (movingFinal)
                {
                    finalY -= (int)Math.Ceiling(vy[i] / 2f);
                    if (finalY <= finalTarget)
                    {
                        finalY = finalTarget;
                        movingFinal = false;
                    }
                }
                vy[i] -= 0.4f;
                if (y[i] >= finalY && passed[i])
                {
                    bounces[i]++;
                    vy[i] = Math.Abs(vy[i] * 2f / 3f);
                    vx[i] -= vx[i] / 2f;
                    int sx = (int)((x[i] + 32f) / 64f), sy = (int)((y[i] + 32f) / 64f);
                    if (loc.isTileOnMap(sx, sy) && loc.doesTileSinkDebris(sx, sy, d.debrisType.Value))
                    {
                        alive[i] = false;
                        continue;
                    }
                }
                int tx = (int)((x[i] + 32f) / 64f), ty = (int)((y[i] + 32f) / 64f);
                if (!hitWall[i] && IsWall(loc, buildings, back, tx, ty))
                {
                    vx[i] = -vx[i];
                    hitWall[i] = true;
                }
                if (y[i] < finalY)
                    passed[i] = true;
                if (bounces[i] > 2)
                {
                    vx[i] = 0f;
                    vy[i] = 0f;
                }
            }
            if (!moving)
                break;
        }

        // 与 Debris.approximatePosition 相同：剩下碎片的平均位置 + 32。
        var total = Vector2.Zero;
        int left = 0;
        for (int i = 0; i < n; i++)
            if (alive[i])
            {
                total += new Vector2(x[i], y[i]);
                left++;
            }
        if (left == 0)
            return new DropForecast(Vector2.Zero, true);
        return new DropForecast(total / left + new Vector2(32f, 32f), false);
    }

    private static bool IsWall(GameLocation loc, Layer? buildings, Layer? back, int x, int y)
    {
        if (!loc.isTileOnMap(x, y) || back?.Tiles[x, y] == null)
            return true;
        return buildings?.Tiles[x, y] != null && loc.doesTileHaveProperty(x, y, "Passable", "Buildings") == null;
    }
}
