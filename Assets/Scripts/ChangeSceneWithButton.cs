using UnityEngine;
using UnityEngine.SceneManagement;
public class ChangeSceneWithButton : MonoBehaviour
{
    public void LoadScene(int index)
    {
        SceneManager.LoadScene(index);
    }
}