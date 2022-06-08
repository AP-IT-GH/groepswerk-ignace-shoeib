using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.SceneManagement;

public class MonsterAgent : Agent
{
    public Vector3 monsterStartPos;
    public float MoveSpeed = 1f;
    public float RotateSpeed = 10f;
    public float MaxSpeed = 10f;
    public GameObject target;
    private Animator _animator;
    public string walkAnimation = "Walk_Cycle_2";
    private readonly string _rotateAnimation = "Sneak_Cycle_1";
    private readonly string _idleAnimation = "Fight_Idle_1";
    public Vector3[] spawnLocations;
    private Vector3 spawnLocation;
    private float distance;
    public bool randomSpawn;
    public bool wallEndsEpisode;
    public bool wallDecreasesReward;
    public void Start()
    {
        monsterStartPos = transform.position;
        _animator = GetComponent<Animator>();
    }
    public override void OnEpisodeBegin()
    {
        transform.position = monsterStartPos;
        transform.eulerAngles = new Vector3(0, 180, 0);
        if (!randomSpawn)
        {
        //    spawnLocation = spawnLocations[Random.Range(0, spawnLocations.Length)];
        //    target.transform.localPosition = spawnLocation;
        }
        else
        {
        //    target.transform.localPosition = new Vector3(Random.Range(-28f, 40), 5, Random.Range(-7f, 64));
        }
    }
    public override void CollectObservations(VectorSensor sensor)
    {
        sensor.AddObservation(transform.position);
        //sensor.AddObservation(transform.localRotation);
        sensor.AddObservation(target.transform.position);
        sensor.AddObservation(distance);
    }

    public override void OnActionReceived(ActionBuffers actionBuffers)
    {
        distance = Vector3.Distance(transform.position, target.transform.position);
        if (distance < 5f)
        {
            SetReward(1f);
            EndEpisode();
        }
        else if (distance > 100f)
        {
            SceneManager.LoadScene(2);
        }
        if (StepCount == MaxStep - 1)
        {
            SetReward(5 / distance);
        }
        var rotate = actionBuffers.ContinuousActions[1];
        var move = actionBuffers.ContinuousActions[0];
        if (move != 0 & !_animator.GetCurrentAnimatorStateInfo(0).IsName($"Armature|{walkAnimation}"))
        {
            _animator.SetTrigger(walkAnimation);
            _animator.speed = MaxSpeed/5;
        }
        else if (rotate != 0 & !_animator.GetCurrentAnimatorStateInfo(0).IsName($"Armature|{_rotateAnimation}"))
        {
            _animator.SetTrigger(_rotateAnimation);
            _animator.speed = RotateSpeed / 50;
        }
        else if (move == 0 & rotate == 0 & !_animator.GetCurrentAnimatorStateInfo(0).IsName($"Armature|{_idleAnimation}"))
        {
            _animator.SetTrigger(_idleAnimation);
            _animator.speed = 1f;
        }
        if (move <0)
        {
            move /= 10;
        }
        transform.Translate(Vector3.forward * move * MoveSpeed * Time.fixedDeltaTime);
        transform.Rotate(0, rotate * RotateSpeed * Time.deltaTime,0);

    }
    public void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.CompareTag("Wall") && wallEndsEpisode)
        {
            SetReward((1 / distance)- 1f);
            EndEpisode();
        }
        if (collision.gameObject.CompareTag("Wall") && wallDecreasesReward)
        {
            AddReward(-.1f);
        }
    }

    public override void Heuristic(in ActionBuffers actionBuffers)
    {
        ActionSegment<float> continuousAction = actionBuffers.ContinuousActions;
        continuousAction[0] = Input.GetAxis("Vertical");
        continuousAction[1] = Input.GetAxis("Horizontal");
    }
}