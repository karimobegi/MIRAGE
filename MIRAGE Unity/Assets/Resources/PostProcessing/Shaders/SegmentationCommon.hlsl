#ifndef SEGMENTATION_COMMON_INCLUDED
#define SEGMENTATION_COMMON_INCLUDED

struct ClassSettings {
    int classId;
    float minRange;
    float maxRange;
    float4 color;
};

StructuredBuffer<ClassSettings> selectedClasses;
int selectedClassesCount;
int _UseDepthFilter;
int _DepthObjectCapacity;
StructuredBuffer<float> objectDepths;

float ReadObjectDepth(int objectId)
{
    float value = -1.0;
    [branch] if (objectId >= 0 && objectId < _DepthObjectCapacity) value = objectDepths[objectId];
    return value;
}

bool IsDepthInRange(float depth, float minimum, float maximum)
{
    return _UseDepthFilter == 0 || (isfinite(depth) && depth > 0 && depth >= minimum && depth <= maximum);
}

bool IsClassSelected(int classIndex)
{
    for (int i = 0; i < selectedClassesCount; i++)
    {
        if (selectedClasses[i].classId == classIndex)
            return true;
    }
    return false;
}

bool IsClassSelected(int classId, float depth)
{
    for (int i = 0; i < selectedClassesCount; i++)
    {
        if (selectedClasses[i].classId == classId)
        {
            if (IsDepthInRange(depth, selectedClasses[i].minRange, selectedClasses[i].maxRange))
            {
                return true;
            }
        }
    }
    return false;
}

float4 GetColorForClassAndDepth(int classId, float depth)
{
    for (int i = 0; i < selectedClassesCount; i++)
    {
        if (selectedClasses[i].classId == classId)
        {
            if (IsDepthInRange(depth, selectedClasses[i].minRange, selectedClasses[i].maxRange))
            {
                return selectedClasses[i].color;
            }
        }
    }
    return float4(0, 0, 0, 0);
}

#endif
