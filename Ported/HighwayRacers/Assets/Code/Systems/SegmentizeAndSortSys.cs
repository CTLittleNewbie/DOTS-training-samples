﻿using Unity.Assertions;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace HighwayRacer
{
    [UpdateAfter(typeof(CarSpawnSys))]
    public class SegmentizeAndSortSys : SystemBase
    {
        public CarBuckets CarBuckets;
        private int nSegments;

        private EntityQuery query;
        
        protected override void OnCreate()
        {
            query = GetEntityQuery(typeof(TrackSegment), typeof(TrackPos), typeof(Speed), typeof(Lane));
            base.OnCreate();
        }

        protected override void OnDestroy()
        {
            CarBuckets.Dispose();
            base.OnDestroy();
        }

        public static bool mergeLeftFrame = true; // toggles every frame: in a frame, we only initiate merges either left or right, not both

        protected override void OnUpdate()
        {
            if (nSegments != RoadSys.nSegments)
            {
                CarBuckets.Dispose();
                CarBuckets = new CarBuckets(RoadSys.nSegments, RoadSys.NumCarsFitInStraightSegment() * 2);
                nSegments = RoadSys.nSegments;
            }

            CarBuckets.Clear();
            var carBuckets = this.CarBuckets;

            mergeLeftFrame = !mergeLeftFrame;

            var segmentJob = new SegmentizeJob()
            {
                CarBuckets = CarBuckets,
                Thresholds = RoadSys.thresholds,
                LastSegmentIdx = RoadSys.nSegments - 1,
                MergingLeftType = GetArchetypeChunkComponentType<MergingLeft>(),
                MergingRightType = GetArchetypeChunkComponentType<MergingRight>(),
                TrackSegmentType = GetArchetypeChunkComponentType<TrackSegment>(),
                TrackPosType = GetArchetypeChunkComponentType<TrackPos>(),
                SpeedType = GetArchetypeChunkComponentType<Speed>(),
                LaneType = GetArchetypeChunkComponentType<Lane>(),
            };

            // todo: might the input dependency fail to include jobs that are still reading CarBuckets?
            // todo: if so, then we have an issue anyway when we clear the buckets
            var jobHandle = segmentJob.ScheduleParallel(query, Dependency);
            
            jobHandle = carBuckets.Sort(jobHandle);
            jobHandle.Complete();

            Dependency = jobHandle;
        }
    }



    [BurstCompile(CompileSynchronously = true)]
    public struct SegmentizeJob : IJobChunk
    {
        [NativeDisableUnsafePtrRestriction] [NativeDisableContainerSafetyRestriction] // todo : is this necessary?
        public CarBuckets CarBuckets;

        [ReadOnly] public ArchetypeChunkComponentType<MergingLeft> MergingLeftType;
        [ReadOnly] public ArchetypeChunkComponentType<MergingRight> MergingRightType;

        public ArchetypeChunkComponentType<TrackSegment> TrackSegmentType;
        [ReadOnly] public ArchetypeChunkComponentType<TrackPos> TrackPosType;
        [ReadOnly] public ArchetypeChunkComponentType<Speed> SpeedType;
        [ReadOnly] public ArchetypeChunkComponentType<Lane> LaneType;

        public int LastSegmentIdx;
        [ReadOnly] public NativeArray<float> Thresholds;

        public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
        {
            sbyte laneOffset = 0; // todo: is sbyte instead of int saving anything? could it even be more costly

            // left and right should be mutually exclusive
            if (chunk.Has(MergingLeftType))
            {
                laneOffset = -1; // lane we came from to the right
            }

            if (chunk.Has(MergingRightType))
            {
                laneOffset = 1; // lane we came from to the left
            }

            var trackPos = chunk.GetNativeArray(TrackPosType);
            var trackSegment = chunk.GetNativeArray(TrackSegmentType);
            var lane = chunk.GetNativeArray(LaneType);
            var speed = chunk.GetNativeArray(SpeedType);

            for (int ent = 0; ent < chunk.Count; ent++)
            {
                trackSegment[ent] = new TrackSegment() {Val = (ushort) LastSegmentIdx}; // last segment gets all the rest (to account for float imprecision)
                for (ushort seg = 0; seg < LastSegmentIdx; seg++)
                {
                    if (trackPos[ent].Val < Thresholds[seg]) // todo: binary search (if num thresholds is large, might make sense)
                    {
                        trackSegment[ent] = new TrackSegment() {Val = seg};
                        break;
                    }
                }

                CarBuckets.AddCar(trackSegment[ent], trackPos[ent], speed[ent], lane[ent]);
                if (laneOffset != 0)
                {
                    CarBuckets.AddCar(trackSegment[ent], trackPos[ent], speed[ent], new Lane()
                    {
                        Val = (byte) (lane[ent].Val + laneOffset),
                    });
                }
            }
        }
    }
}