using System;

public struct A
{
	public int a;
	public long b;
	public string c;

	public float f;
	public static string Hello = "Hello World";

	public A (int a, long b, string c)
	{
		this.a = a;
		this.b = b;
		this.c = c;
		this.f = (float) b / (float) a;
	}

	public void Test ()
	{
		Console.WriteLine (f);
		Console.WriteLine (Hello);
	}
}

public class B
{
	public int a;
	public long b;
	public string c;

	public static float Test = 3.14F;

	public float Property {
		get { return Test; }
	}

	public B (int a, long b, string c)
	{
		this.a = a;
		this.b = b;
		this.c = c;
	}

	public void Foo ()
	{
		Console.WriteLine (a);
		Console.WriteLine (Test);
	}

	public void Foo (string hello)
	{
		Console.WriteLine (hello);
	}
}

public class C : B
{
	public new int a;
	public float f;

	public C (int a, long b, string c, float f, int new_a)
		: base (a, b, c)
	{
		this.f = f;
		this.a = new_a;
	}

	public class Nested
	{
		public static long Foo = 512;
	}
}

struct D
{
	public A a;
	public B b;
	public C c;
	public string[] s;

	public D (A a, B b, C c, params string[] args)
	{
		this.a = a;
		this.b = b;
		this.c = c;
		this.s = args;
	}
}

struct E
{
	public int a;

	public E (int a)
	{
		this.a = a;
	}

	public long Foo (int a)
	{
		return a;
	}
}

public class X
{
	public static string Hello = "Hello World";

	public static void Simple ()
	{
		int a = 5;
		long b = 7;
		float f = (float) a / (float) b;

		string hello = "Hello World";

		// Breakpoint 1
		Console.WriteLine (a);
		Console.WriteLine (b);
		Console.WriteLine (f);
		Console.WriteLine (hello);
	}

	public static void BoxedValueType ()
	{
		int a = 5;
		object boxed_a = a;

		// Breakpoint 2
		Console.WriteLine (a);
		Console.WriteLine (boxed_a);
	}

	public static void BoxedReferenceType ()
	{
		string hello = "Hello World";
		object boxed_hello = hello;

		// Breakpoint 3
		Console.WriteLine (hello);
		Console.WriteLine (boxed_hello);
	}

	public static void SimpleArray ()
	{
		int[] a = { 3, 4, 5 };

		Console.WriteLine (a [2]);
	}

	public static void MultiValueTypeArray ()
	{
		int[,] a = { { 6, 7, 8 }, { 9, 10, 11 } };

		Console.WriteLine (a [1,2]);
	}

	public static void StringArray ()
	{
		string[] a = { "Hello", "World" };

		Console.WriteLine (a);
	}

	public static void MultiStringArray ()
	{
		string[,] a = { { "Hello", "World" }, { "New York", "Boston" },
				{ "Ximian", "Monkeys" } };

		Console.WriteLine (a);
	}

	public static void StructType ()
	{
		A a = new A (5, 256, "New England Patriots");
		a.Test ();
		Console.WriteLine (a);
	}

	public static void ClassType ()
	{
		B b = new B (5, 256, "New England Patriots");
		b.Foo ();
		Console.WriteLine (b);
	}

	public static void InheritedClassType ()
	{
		C c = new C (5, 256, "New England Patriots", 3.14F, 8);
		Console.WriteLine (c.a);

		B b = c;
		Console.WriteLine (b.a);
	}

	public static void ComplexStructType ()
	{
		A a = new A (5, 256, "New England Patriots");
		B b = new B (5, 256, "New England Patriots");
		C c = new C (5, 256, "New England Patriots", 3.14F, 8);

		D d = new D (a, b, c, "Eintracht Trier");
		Console.WriteLine (d.s [0]);
	}

	public static void FunctionStructType ()
	{
		E e = new E (9);

		e.Foo (10);
		Console.WriteLine (e.a);
	}

	public static void Main ()
	{
		Simple ();
		BoxedValueType ();
		BoxedReferenceType ();
		SimpleArray ();
		MultiValueTypeArray ();
		StringArray ();
		MultiStringArray ();
		StructType ();
		ClassType ();
		InheritedClassType ();
		ComplexStructType ();
		FunctionStructType ();
	}
}