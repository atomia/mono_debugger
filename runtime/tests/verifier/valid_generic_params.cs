public class GenericClass<T>
{
	public void SimpleMethod ()
	{
		T a = default (T);
		T b = a;
		T c;
		if (SimpleClass.cond)
			c = a;
		else
			c = b;		
	}

	public void SimpleMethod2 (ref T a)
	{
		T b = a;
		T c;
		if (SimpleClass.cond)
			c = a;
		else
			c = b;
		a = c;
	}
}

public class SimpleClass
{
	public static bool cond;
	public void GenericMethod<T> () {
		T a = default (T);
		T b = a;
	}

	public void GenericMethod<T> (T t) {
		T a = t;
		t = a;
	}

}

public class Driver
{
	public static int Main ()
	{
		new GenericClass<int>().SimpleMethod ();
		new SimpleClass().GenericMethod<int>();
		new SimpleClass().GenericMethod<int>(10);

		return 0;
	}
}
